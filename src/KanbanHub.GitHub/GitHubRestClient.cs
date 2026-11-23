using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KanbanHub.Domain.GitHub;

namespace KanbanHub.GitHub;

/// <summary>
/// The GitHub REST API as the sync uses it. Repositories are addressed by id (<c>repositories/{id}/...</c>,
/// the form GitHub itself redirects to after a rename), so a renamed repository keeps syncing.
/// </summary>
public sealed partial class GitHubRestClient(IHttpClientFactory http, GitHubAppTokenProvider tokens, TimeProvider clock) : IGitHubClient
{
    public const string ClientName = "github";

    private const int PageSize = 100;

    public async Task<RepositoryInfo?> FindRepositoryAsync(string fullName, CancellationToken cancellationToken)
    {
        string appJwt = await tokens.CreateAppJwtAsync(cancellationToken);
        if (await SendAsync(new Call(HttpMethod.Get, $"repos/{fullName}/installation"), appJwt, cancellationToken) is not { } installation)
            return null;

        long installationId = installation.GetProperty("id").GetInt64();
        return await CallAsync(installationId, new Call(HttpMethod.Get, $"repos/{fullName}"), cancellationToken) is { } repository
            ? new RepositoryInfo(new RepoRef(installationId, repository.GetProperty("id").GetInt64()), repository.GetProperty("full_name").GetString()!)
            : null;
    }

    public async Task<IssueSnapshot?> FindIssueAsync(IssueRef issue, CancellationToken cancellationToken) =>
        await CallAsync(issue.InstallationId, new Call(HttpMethod.Get, IssueUrl(issue)), cancellationToken) is { } found
            ? GitHubJson.ToSnapshot(found, issue.Repo)
            : null;

    public async Task<IssueSnapshot> CreateIssueAsync(RepoRef repository, IssueChange content, CancellationToken cancellationToken)
    {
        var call = new Call(HttpMethod.Post, $"{RepoUrl(repository)}/issues", BodyOf(content));
        return GitHubJson.ToSnapshot(await MustCallAsync(repository.InstallationId, call, cancellationToken), repository);
    }

    public async Task UpdateIssueAsync(IssueRef issue, IssueChange change, CancellationToken cancellationToken) =>
        await MustCallAsync(issue.InstallationId, new Call(HttpMethod.Patch, IssueUrl(issue), BodyOf(change)), cancellationToken);

    public async Task<long> AddCommentAsync(IssueRef issue, string body, CancellationToken cancellationToken)
    {
        var call = new Call(HttpMethod.Post, $"{IssueUrl(issue)}/comments", new JsonObject { ["body"] = body });
        return (await MustCallAsync(issue.InstallationId, call, cancellationToken)).GetProperty("id").GetInt64();
    }

    public async Task<bool> EditCommentAsync(RepoRef repository, long commentId, string body, CancellationToken cancellationToken)
    {
        var call = new Call(HttpMethod.Patch, $"{RepoUrl(repository)}/issues/comments/{commentId}", new JsonObject { ["body"] = body });
        return await CallAsync(repository.InstallationId, call, cancellationToken) is not null;
    }

    public async Task<IssuePage> ListIssuesAsync(RepoRef repository, IssueQuery query, CancellationToken cancellationToken)
    {
        string token = await tokens.GetInstallationTokenAsync(repository.InstallationId, cancellationToken);
        using HttpResponseMessage response = await SendRawAsync(new Call(HttpMethod.Get, ListUrl(repository, query)), token, cancellationToken);
        response.EnsureSuccessStatusCode();

        JsonElement issues = await ReadAsync(response, cancellationToken);
        return new IssuePage(
            [.. issues.EnumerateArray().Select(issue => GitHubJson.ToSnapshot(issue, repository))],
            LastPageOf(response, query.Page));
    }

    public async Task<long> SaveCheckRunAsync(RepoRef repository, long? checkRunId, CheckRun run, CancellationToken cancellationToken)
    {
        Call call = checkRunId is { } existing
            ? new Call(HttpMethod.Patch, $"{RepoUrl(repository)}/check-runs/{existing}", BodyOf(run, includeCommit: false))
            : new Call(HttpMethod.Post, $"{RepoUrl(repository)}/check-runs", BodyOf(run, includeCommit: true));

        return (await MustCallAsync(repository.InstallationId, call, cancellationToken)).GetProperty("id").GetInt64();
    }

    /// <summary>One request: what to do, where, and with which body.</summary>
    private sealed record Call(HttpMethod Method, string Url, JsonObject? Body = null);

    private async Task<JsonElement> MustCallAsync(long installationId, Call call, CancellationToken cancellationToken) =>
        await CallAsync(installationId, call, cancellationToken)
        ?? throw new HttpRequestException($"GitHub has nothing at {call.Url}.", inner: null, HttpStatusCode.NotFound);

    /// <summary>Calls GitHub as the installation. Returns null when the thing asked for isn't there (404 or 410).</summary>
    private async Task<JsonElement?> CallAsync(long installationId, Call call, CancellationToken cancellationToken) =>
        await SendAsync(call, await tokens.GetInstallationTokenAsync(installationId, cancellationToken), cancellationToken);

    private async Task<JsonElement?> SendAsync(Call call, string token, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendRawAsync(call, token, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;

        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRawAsync(Call call, string token, CancellationToken cancellationToken)
    {
        using HttpClient client = http.CreateClient(ClientName);
        using var request = new HttpRequestMessage(call.Method, call.Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (call.Body is not null) request.Content = new StringContent(call.Body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        ThrowIfRateLimited(response);
        return response;
    }

    /// <summary>On a 403 or 429 GitHub says when to come back, in <c>retry-after</c> or <c>x-ratelimit-reset</c> (section 9.6).</summary>
    private void ThrowIfRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return;

        DateTimeOffset? retryAt =
            response.Headers.RetryAfter?.Delta is { } wait ? clock.GetUtcNow() + wait
            : HeaderOf(response, "x-ratelimit-remaining") == "0" && long.TryParse(HeaderOf(response, "x-ratelimit-reset"), out long reset)
                ? DateTimeOffset.FromUnixTimeSeconds(reset)
            : null;
        if (retryAt is null) return;

        response.Dispose();
        throw new GitHubRateLimitedException(retryAt.Value);
    }

    private static string? HeaderOf(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private static string RepoUrl(RepoRef repository) => $"repositories/{repository.RepositoryId}";

    private static string IssueUrl(IssueRef issue) => $"{RepoUrl(issue.Repo)}/issues/{issue.Number}";

    /// <summary>Open issues with a label for a backfill; everything updated since a moment for the reconciler.</summary>
    private static string ListUrl(RepoRef repository, IssueQuery query)
    {
        var url = new StringBuilder($"{RepoUrl(repository)}/issues?per_page={PageSize}&page={query.Page}&sort=updated&direction=asc");
        url.Append(query.UpdatedSince is null ? "&state=open" : "&state=all");
        if (query.Label is { } label) url.Append("&labels=").Append(Uri.EscapeDataString(label));
        if (query.UpdatedSince is { } since) url.Append("&since=").Append(Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        return url.ToString();
    }

    /// <summary>The page the <c>Link</c> header calls "last"; without one, this page is the last.</summary>
    private static int LastPageOf(HttpResponseMessage response, int currentPage) =>
        HeaderOf(response, "Link") is { } link && LastPage().Match(link) is { Success: true } match
            ? int.Parse(match.Groups["page"].ValueSpan, CultureInfo.InvariantCulture)
            : currentPage;

    private static JsonObject BodyOf(IssueChange change)
    {
        var body = new JsonObject();
        if (change.Title is not null) body["title"] = change.Title;
        if (change.Body is not null) body["body"] = change.Body;
        if (change.Labels is not null) body["labels"] = new JsonArray([.. change.Labels.Select(label => (JsonNode)label)]);
        if (change.State is { } state)
        {
            body["state"] = state is IssueState.Open ? "open" : "closed";
            if (state is not IssueState.Open) body["state_reason"] = state is IssueState.NotPlanned ? "not_planned" : "completed";
        }

        return body;
    }

    private static JsonObject BodyOf(CheckRun run, bool includeCommit)
    {
        var body = new JsonObject
        {
            ["name"] = CheckRun.Name,
            ["status"] = "completed",
            ["conclusion"] = run.Passed ? "success" : "failure",
            ["output"] = new JsonObject
            {
                ["title"] = run.Passed ? "Done rule met" : "Done rule not met yet",
                ["summary"] = run.Summary
            }
        };
        if (includeCommit) body["head_sha"] = run.HeadSha;
        return body;
    }

    [GeneratedRegex(@"[?&]page=(?<page>\d+)[^>]*>;\s*rel=""last""")]
    private static partial Regex LastPage();
}
