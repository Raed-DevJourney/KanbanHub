using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KanbanHub.Domain.GitHub;

namespace KanbanHub.GitHub;

/// <summary>The <c>GitHub</c> configuration section. The secret comes from a Key Vault reference; the key never leaves the vault.</summary>
public sealed record class GitHubAppOptions
{
    public const string Section = "GitHub";

    /// <summary>The app's client id: the issuer of the JWT it signs.</summary>
    public string ClientId { get; init; } = "";

    /// <summary>The Key Vault key that holds the app's private key, as a key identifier URI.</summary>
    public string KeyId { get; init; } = "";

    public string WebhookSecret { get; init; } = "";

    /// <summary>Still accepted while a rotation is under way (the dual-secret window of section 9.1).</summary>
    public string? PreviousWebhookSecret { get; init; }

    /// <summary>The login the app acts under in GitHub, for example <c>kanbanhub[bot]</c>.</summary>
    public string BotLogin { get; init; } = "";

    /// <summary>The Entra object id the app acts as inside KanbanHub.</summary>
    public Guid PrincipalId { get; init; }

    /// <summary>Where the web app lives, for the link in the status comment and the "Connect GitHub" callback.</summary>
    public Uri? WebBaseUrl { get; init; }

    /// <summary>The app's client secret, for "Connect GitHub" (section 9.1). Comes from a Key Vault reference.</summary>
    public string ClientSecret { get; init; } = "";

    /// <summary>The Key Vault key that encrypts people's refresh tokens, as a key identifier URI.</summary>
    public string TokenKeyId { get; init; } = "";

    /// <summary>The web page GitHub sends a person back to after they approved the app.</summary>
    public string? CallbackUrl => WebBaseUrl is null ? null : new Uri(WebBaseUrl, "github/callback").ToString();

    public SyncSettings ToSyncSettings() => new(PrincipalId, BotLogin, WebBaseUrl);
}

/// <summary>
/// Checks <c>X-Hub-Signature-256</c>: HMAC-SHA256 of the raw body, compared in constant time,
/// as GitHub's documentation requires (section 9.2).
/// </summary>
public sealed class WebhookVerifier(GitHubAppOptions options)
{
    private const string Prefix = "sha256=";

    public bool IsGenuine(byte[] body, string signatureHeader) =>
        IsSignedWith(options.WebhookSecret, body, signatureHeader)
        || IsSignedWith(options.PreviousWebhookSecret, body, signatureHeader);

    private static bool IsSignedWith(string? secret, byte[] body, string signatureHeader)
    {
        if (string.IsNullOrEmpty(secret) || !signatureHeader.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        Span<byte> claimed = stackalloc byte[HMACSHA256.HashSizeInBytes];
        OperationStatus decoded = Convert.FromHexString(signatureHeader.AsSpan(Prefix.Length), claimed, out _, out int written);
        if (decoded is not OperationStatus.Done || written != claimed.Length) return false;

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, expected);
        return CryptographicOperations.FixedTimeEquals(expected, claimed);
    }
}

/// <summary>Reads the parts of GitHub's JSON the sync uses. Shared by the webhook parser and the REST client.</summary>
internal static class GitHubJson
{
    public static string? Text(JsonElement element, params ReadOnlySpan<string> path) =>
        Find(element, path) is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;

    public static long? Number(JsonElement element, params ReadOnlySpan<string> path) =>
        Find(element, path) is { ValueKind: JsonValueKind.Number } found ? found.GetInt64() : null;

    public static bool IsTrue(JsonElement element, params ReadOnlySpan<string> path) =>
        Find(element, path) is { ValueKind: JsonValueKind.True };

    public static JsonElement? Find(JsonElement element, ReadOnlySpan<string> path)
    {
        foreach (string name in path)
        {
            if (element.ValueKind is not JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
        }

        return element;
    }

    public static IssueSnapshot ToSnapshot(JsonElement issue, RepoRef repository) => new()
    {
        Issue = new IssueRef(repository.InstallationId, repository.RepositoryId, (int)(Number(issue, "number") ?? 0)),
        NodeId = Text(issue, "node_id") ?? "",
        Title = Text(issue, "title") ?? "",
        Body = Text(issue, "body"),
        Labels = LabelsOf(issue),
        State = StateOf(issue),
        UpdatedAt = issue.TryGetProperty("updated_at", out JsonElement updated) && updated.TryGetDateTimeOffset(out DateTimeOffset at) ? at : default,
        IsPullRequest = issue.TryGetProperty("pull_request", out _)
    };

    /// <summary>GitHub sends labels as objects with a name; older payloads send plain strings.</summary>
    private static IReadOnlyList<string> LabelsOf(JsonElement issue) =>
        issue.TryGetProperty("labels", out JsonElement labels) && labels.ValueKind is JsonValueKind.Array
            ? [.. labels.EnumerateArray().Select(label => label.ValueKind is JsonValueKind.String ? label.GetString() : Text(label, "name")).OfType<string>()]
            : [];

    private static IssueState StateOf(JsonElement issue) => (Text(issue, "state"), Text(issue, "state_reason")) switch
    {
        ("closed", "not_planned") => IssueState.NotPlanned,
        ("closed", _) => IssueState.Completed,
        _ => IssueState.Open
    };
}

/// <summary>Turns webhook payloads into the events the sync understands, and anything else into <see cref="Unhandled"/>.</summary>
public sealed class GitHubWebhookParser : IGitHubEventParser
{
    private const string Unassigned = "unassigned";

    private static readonly string[] WriteAccess = ["OWNER", "MEMBER", "COLLABORATOR"];

    /// <summary>The pull request actions that change what a card shows or what its check run is attached to.</summary>
    private static readonly string[] PullRequestActions =
        ["opened", "edited", "synchronize", "reopened", "closed", "ready_for_review", "converted_to_draft"];

    public ParsedDelivery Parse(RawDelivery delivery)
    {
        using JsonDocument document = JsonDocument.Parse(delivery.Payload);
        JsonElement root = document.RootElement;
        string sender = GitHubJson.Text(root, "sender", "login") ?? "";

        return new ParsedDelivery(delivery.DeliveryId, delivery.EventName, sender, EventOf(delivery.EventName, root));
    }

    /// <summary>"repo:{id}/issue:{number}", as in section 9.7; number 0 groups what isn't about one issue.</summary>
    public string SessionKeyOf(RawDelivery delivery)
    {
        using JsonDocument document = JsonDocument.Parse(delivery.Payload);
        JsonElement root = document.RootElement;
        long repository = GitHubJson.Number(root, "repository", "id") ?? 0;
        long number = GitHubJson.Number(root, "issue", "number") ?? GitHubJson.Number(root, "pull_request", "number") ?? 0;

        return $"repo:{repository}/issue:{number}";
    }

    private static GitHubEvent EventOf(string eventName, JsonElement root)
    {
        if (RepositoryOf(root) is not { } repository) return new Unhandled();
        string action = GitHubJson.Text(root, "action") ?? "";

        return eventName switch
        {
            "issues" => IssueEvent(root, repository, action),
            "issue_comment" when action == "created" => CommentEvent(root, repository),
            "pull_request" when PullRequestActions.Contains(action) => PullRequestEvent(root, repository),
            "repository" when action == "renamed" => new RepositoryRenamed(repository.RepositoryId, GitHubJson.Text(root, "repository", "full_name") ?? ""),
            _ => new Unhandled()
        };
    }

    private static RepoRef? RepositoryOf(JsonElement root) =>
        GitHubJson.Number(root, "installation", "id") is { } installation && GitHubJson.Number(root, "repository", "id") is { } repository
            ? new RepoRef(installation, repository)
            : null;

    private static GitHubEvent IssueEvent(JsonElement root, RepoRef repository, string action)
    {
        if (action == Unassigned) return UnassignedEvent(root, repository);

        bool isKnown = action is IssueChanged.Opened or IssueChanged.Edited or IssueChanged.Labeled
            or IssueChanged.Unlabeled or IssueChanged.Closed or IssueChanged.Reopened;
        if (!isKnown || GitHubJson.Find(root, ["issue"]) is not { } issue) return new Unhandled();

        return new IssueChanged(GitHubJson.ToSnapshot(issue, repository), action, GitHubJson.Text(root, "label", "name"));
    }

    private static GitHubEvent CommentEvent(JsonElement root, RepoRef repository)
    {
        if (GitHubJson.Find(root, ["issue"]) is not { } issue || issue.TryGetProperty("pull_request", out _)) return new Unhandled();
        if (GitHubJson.Number(issue, "number") is not { } number || GitHubJson.Number(root, "comment", "id") is not { } commentId)
            return new Unhandled();

        string association = GitHubJson.Text(root, "comment", "author_association") ?? "";
        return new IssueCommented(
            new IssueRef(repository.InstallationId, repository.RepositoryId, (int)number), commentId,
            GitHubJson.Text(root, "comment", "body") ?? "", GitHubJson.Text(root, "comment", "user", "login") ?? "",
            AuthorCanWrite: WriteAccess.Contains(association));
    }

    private static GitHubEvent UnassignedEvent(JsonElement root, RepoRef repository) =>
        GitHubJson.Number(root, "issue", "number") is { } number && GitHubJson.Text(root, "assignee", "login") is { } login
            ? new AgentUnassigned(new IssueRef(repository.InstallationId, repository.RepositoryId, (int)number), login)
            : new Unhandled();

    private static GitHubEvent PullRequestEvent(JsonElement root, RepoRef repository)
    {
        if (GitHubJson.Find(root, ["pull_request"]) is not { } pullRequest) return new Unhandled();
        if (GitHubJson.Number(pullRequest, "number") is not { } number || GitHubJson.Text(pullRequest, "head", "sha") is not { } sha)
            return new Unhandled();

        return new PullRequestChanged(repository, (int)number, sha, GitHubJson.Text(pullRequest, "body"))
        {
            State = StateOf(pullRequest),
            IsDraft = GitHubJson.IsTrue(pullRequest, "draft"),
            AuthorLogin = GitHubJson.Text(pullRequest, "user", "login") ?? ""
        };
    }

    private static PullRequestState StateOf(JsonElement pullRequest) =>
        GitHubJson.Text(pullRequest, "state") != "closed" ? PullRequestState.Open
        : GitHubJson.IsTrue(pullRequest, "merged") ? PullRequestState.Merged
        : PullRequestState.Closed;
}
