using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Domain.GitHub;
using KanbanHub.GitHub;
using Microsoft.Extensions.Caching.Memory;

namespace KanbanHub.Api.Tests.GitHub;

public class WebhookVerifierTests
{
    private const string Secret = "It's a Secret to Everybody";

    private static readonly byte[] Body = "Hello, World!"u8.ToArray();
    private static readonly WebhookVerifier Verifier = new(new GitHubAppOptions { WebhookSecret = Secret });

    [Fact]
    public void The_example_from_githubs_documentation_validates()
    {
        Assert.True(Verifier.IsGenuine(Body, "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17"));
    }

    [Fact]
    public void A_body_that_changed_after_signing_is_refused()
    {
        Assert.False(Verifier.IsGenuine("Hello, World?"u8.ToArray(), Sign(Secret, Body)));
    }

    [Fact]
    public void During_a_rotation_the_previous_secret_still_signs()
    {
        var rotating = new WebhookVerifier(new GitHubAppOptions { WebhookSecret = "the new one", PreviousWebhookSecret = Secret });

        Assert.True(rotating.IsGenuine(Body, Sign(Secret, Body)));
        Assert.True(rotating.IsGenuine(Body, Sign("the new one", Body)));
        Assert.False(rotating.IsGenuine(Body, Sign("an older one", Body)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    [InlineData("sha1=757107ea0eb2509fc211221cce984b8a37570b6d")]
    [InlineData("sha256=not-hex")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17ff")]
    public void A_malformed_signature_is_refused(string header)
    {
        Assert.False(Verifier.IsGenuine(Body, header));
    }

    [Fact]
    public void Without_a_configured_secret_nothing_is_genuine()
    {
        var unconfigured = new WebhookVerifier(new GitHubAppOptions());

        Assert.False(unconfigured.IsGenuine(Body, Sign("", Body)));
    }

    private static string Sign(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
}

/// <summary>Payloads in the shape GitHub sends them, cut down to the members the sync reads.</summary>
public class GitHubWebhookParserTests
{
    private const string Envelope = """
        "repository": { "id": 1296269, "full_name": "octocat/Hello-World" },
        "installation": { "id": 99 },
        "sender": { "login": "octocat" }
        """;

    private static readonly GitHubWebhookParser Parser = new();

    [Fact]
    public void A_labeled_issue_becomes_an_issue_change_with_the_label_and_the_issues_state()
    {
        var delivery = new RawDelivery("d-1", "issues", $$"""
            {
              "action": "labeled",
              "issue": {
                "number": 42, "node_id": "I_kwDOABCD", "title": "Checkout fails", "body": null,
                "labels": [ { "name": "bug" }, { "name": "kanban:implement" } ],
                "state": "open", "state_reason": null, "updated_at": "2026-11-16T09:30:00Z"
              },
              "label": { "name": "kanban:implement" },
              {{Envelope}}
            }
            """);

        ParsedDelivery parsed = Parser.Parse(delivery);

        IssueChanged change = Assert.IsType<IssueChanged>(parsed.Event.Value);
        Assert.Equal(("d-1", "issues", "octocat"), (parsed.DeliveryId, parsed.EventName, parsed.SenderLogin));
        Assert.Equal((IssueChanged.Labeled, "kanban:implement"), (change.Action, change.Label));
        Assert.Equal(new IssueRef(99, 1296269, 42), change.Issue.Issue);
        Assert.Equal(("I_kwDOABCD", "Checkout fails", null, IssueState.Open, false),
            (change.Issue.NodeId, change.Issue.Title, change.Issue.Body, change.Issue.State, change.Issue.IsPullRequest));
        Assert.Equal(["bug", "kanban:implement"], change.Issue.Labels);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero), change.Issue.UpdatedAt);
        Assert.Equal("repo:1296269/issue:42", Parser.SessionKeyOf(delivery));
    }

    [Theory]
    [InlineData("completed", IssueState.Completed)]
    [InlineData("not_planned", IssueState.NotPlanned)]
    public void A_closed_issue_says_how_it_was_closed(string reason, IssueState expected)
    {
        ParsedDelivery parsed = Parser.Parse(new RawDelivery("d-2", "issues", $$"""
            { "action": "closed", "issue": { "number": 7, "title": "T", "state": "closed", "state_reason": "{{reason}}" }, {{Envelope}} }
            """));

        Assert.Equal(expected, Assert.IsType<IssueChanged>(parsed.Event.Value).Issue.State);
    }

    [Theory]
    [InlineData("OWNER", true)]
    [InlineData("COLLABORATOR", true)]
    [InlineData("CONTRIBUTOR", false)]
    [InlineData("NONE", false)]
    public void A_comment_on_an_issue_carries_its_author_and_whether_they_can_write(string association, bool canWrite)
    {
        ParsedDelivery parsed = Parser.Parse(new RawDelivery("d-3", "issue_comment", $$"""
            {
              "action": "created",
              "issue": { "number": 42, "title": "T", "state": "open" },
              "comment": { "id": 987654321, "body": "/kanban move next", "author_association": "{{association}}", "user": { "login": "hubot" } },
              {{Envelope}}
            }
            """));

        Assert.Equal(
            new IssueCommented(new IssueRef(99, 1296269, 42), 987654321, "/kanban move next", "hubot", canWrite),
            parsed.Event.Value);
    }

    [Fact]
    public void A_pull_request_becomes_a_change_with_its_latest_commit()
    {
        var delivery = new RawDelivery("d-4", "pull_request", $$"""
            { "action": "synchronize", "pull_request": { "number": 77, "body": "Fixes #42", "head": { "sha": "abc123" } }, {{Envelope}} }
            """);

        Assert.Equal(new PullRequestChanged(new RepoRef(99, 1296269), 77, "abc123", "Fixes #42"), Parser.Parse(delivery).Event.Value);
        Assert.Equal("repo:1296269/issue:77", Parser.SessionKeyOf(delivery));
    }

    [Theory]
    [InlineData("opened", "open", false, true, PullRequestState.Open, true)]
    [InlineData("ready_for_review", "open", false, false, PullRequestState.Open, false)]
    [InlineData("closed", "closed", true, false, PullRequestState.Merged, false)]
    [InlineData("closed", "closed", false, false, PullRequestState.Closed, false)]
    public void A_pull_request_says_who_opened_it_and_whether_it_is_a_draft_merged_or_closed(
        string action, string state, bool merged, bool draft, PullRequestState expectedState, bool expectedDraft)
    {
        ParsedDelivery parsed = Parser.Parse(new RawDelivery("d-8", "pull_request", $$"""
            {
              "action": "{{action}}",
              "pull_request": {
                "number": 78, "body": "Fixes #42", "state": "{{state}}", "merged": {{(merged ? "true" : "false")}},
                "draft": {{(draft ? "true" : "false")}}, "user": { "login": "Copilot", "type": "Bot" }, "head": { "sha": "def456" }
              },
              {{Envelope}}
            }
            """));

        PullRequestChanged change = Assert.IsType<PullRequestChanged>(parsed.Event.Value);
        Assert.Equal((78, "def456", "Copilot", expectedState, expectedDraft),
            (change.Number, change.HeadSha, change.AuthorLogin, change.State, change.IsDraft));
    }

    /// <summary>Unassigning the agent is how people stop it on GitHub, so the sync wants to hear about it.</summary>
    [Fact]
    public void Unassigning_someone_from_an_issue_names_who_was_unassigned()
    {
        ParsedDelivery parsed = Parser.Parse(new RawDelivery("d-9", "issues", $$"""
            {
              "action": "unassigned",
              "issue": { "number": 42, "title": "T", "state": "open" },
              "assignee": { "login": "Copilot", "type": "Bot" },
              {{Envelope}}
            }
            """));

        Assert.Equal(new AgentUnassigned(new IssueRef(99, 1296269, 42), "Copilot"), parsed.Event.Value);
    }

    [Fact]
    public void A_renamed_repository_carries_its_new_name()
    {
        ParsedDelivery parsed = Parser.Parse(new RawDelivery("d-5", "repository", $$"""{ "action": "renamed", {{Envelope}} }"""));

        Assert.Equal(new RepositoryRenamed(1296269, "octocat/Hello-World"), parsed.Event.Value);
    }

    [Theory]
    [InlineData("ping", """{ "zen": "Design for failure." }""")]
    [InlineData("issues", """{ "action": "pinned", "issue": { "number": 1 }, "repository": { "id": 1 }, "installation": { "id": 1 } }""")]
    [InlineData("issues", """{ "action": "opened", "issue": { "number": 1 }, "repository": { "id": 1 } }""")]
    [InlineData("issue_comment", """{ "action": "deleted", "issue": { "number": 1 }, "repository": { "id": 1 }, "installation": { "id": 1 } }""")]
    [InlineData("issue_comment", """{ "action": "created", "issue": { "number": 1, "pull_request": {} }, "comment": { "id": 5 }, "repository": { "id": 1 }, "installation": { "id": 1 } }""")]
    [InlineData("pull_request", """{ "action": "labeled", "pull_request": { "number": 1, "head": { "sha": "a" } }, "repository": { "id": 1 }, "installation": { "id": 1 } }""")]
    [InlineData("pull_request", """{ "action": "closed", "pull_request": { "number": 1 }, "repository": { "id": 1 }, "installation": { "id": 1 } }""")]
    [InlineData("issues", """{ "action": "unassigned", "issue": { "number": 1 }, "repository": { "id": 1 }, "installation": { "id": 1 } }""")]
    [InlineData("issues", """{ "action": "assigned", "issue": { "number": 1 }, "assignee": { "login": "Copilot" }, "repository": { "id": 1 }, "installation": { "id": 1 } }""")]
    [InlineData("star", """{ "action": "created", "repository": { "id": 1 }, "installation": { "id": 1 } }""")]
    public void Everything_else_is_left_unhandled(string eventName, string payload)
    {
        Assert.IsType<Unhandled>(Parser.Parse(new RawDelivery("d-6", eventName, payload)).Event.Value);
    }

    [Fact]
    public void A_delivery_about_no_issue_still_has_a_session()
    {
        Assert.Equal("repo:0/issue:0", Parser.SessionKeyOf(new RawDelivery("d-7", "ping", "{}")));
    }
}

/// <summary>The REST client and the token provider against a stand-in for api.github.com that records what it is sent.</summary>
public sealed class GitHubRestClientTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);
    private static readonly IssueRef Issue = new(InstallationId: 5, RepositoryId: 1296269, Number: 42);

    private readonly FakeGitHubApi api = new();
    private readonly RSA key = RSA.Create(2048);
    private readonly MemoryCache cache = new(new MemoryCacheOptions());
    private readonly GitHubAppTokenProvider tokens;
    private readonly GitHubRestClient client;

    public GitHubRestClientTests()
    {
        var clock = new FixedClock(Now);
        tokens = new GitHubAppTokenProvider(new LocalSigner(key), api, cache, new GitHubAppOptions { ClientId = "Iv23liTestApp" }, clock);
        client = new GitHubRestClient(api, tokens, clock);
        // The cache keeps the token by the real clock, so the token's expiry is an hour from the real now.
        api.On(HttpMethod.Post, "app/installations/5/access_tokens", $$"""{ "token": "ghs_installation", "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):O}}" }""");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        api.Dispose();
        key.Dispose();
        cache.Dispose();
    }

    [Fact]
    public async Task The_app_jwt_is_signed_with_the_apps_key_names_the_app_and_lives_under_ten_minutes()
    {
        string[] parts = (await tokens.CreateAppJwtAsync(Ct)).Split('.');

        using JsonDocument header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
        using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        bool verified = key.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.Equal(3, parts.Length);
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("Iv23liTestApp", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal(Now.AddSeconds(-60).ToUnixTimeSeconds(), payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(Now.AddMinutes(9).ToUnixTimeSeconds(), payload.RootElement.GetProperty("exp").GetInt64());
        Assert.True(verified);
    }

    [Fact]
    public async Task An_installation_token_is_asked_for_with_the_app_jwt_once_and_then_reused()
    {
        string first = await tokens.GetInstallationTokenAsync(5, Ct);
        string second = await tokens.GetInstallationTokenAsync(5, Ct);

        SentRequest request = Assert.Single(api.Requests);
        Assert.Equal(("ghs_installation", "ghs_installation"), (first, second));
        Assert.Equal(3, request.BearerToken.Split('.').Length);
    }

    [Fact]
    public async Task Updating_an_issue_sends_only_what_changed_to_the_repository_by_its_id()
    {
        api.On(HttpMethod.Patch, "repositories/1296269/issues/42", "{}");

        await client.UpdateIssueAsync(Issue, new IssueChange { Labels = ["kanban:done", "bug"], State = IssueState.NotPlanned }, Ct);

        SentRequest request = api.Requests[^1];
        Assert.Equal((HttpMethod.Patch, "repositories/1296269/issues/42", "ghs_installation"), (request.Method, request.Path, request.BearerToken));
        Assert.Equal("""{"labels":["kanban:done","bug"],"state":"closed","state_reason":"not_planned"}""", request.Body);
    }

    [Fact]
    public async Task Creating_an_issue_and_commenting_return_what_github_made()
    {
        api.On(HttpMethod.Post, "repositories/1296269/issues", """{ "number": 43, "node_id": "I_43", "title": "New", "body": "Text", "state": "open", "labels": [] }""");
        api.On(HttpMethod.Post, "repositories/1296269/issues/43/comments", """{ "id": 1001 }""");
        api.On(HttpMethod.Patch, "repositories/1296269/issues/comments/1001", "{}");

        IssueSnapshot created = await client.CreateIssueAsync(Issue.Repo, new IssueChange { Title = "New", Body = "Text" }, Ct);
        long commentId = await client.AddCommentAsync(created.Issue, "Hello", Ct);
        bool edited = await client.EditCommentAsync(Issue.Repo, commentId, "Hello again", Ct);
        bool deletedOne = await client.EditCommentAsync(Issue.Repo, commentId: 404, "Anyone?", Ct);

        Assert.Equal((new IssueRef(5, 1296269, 43), "New", "Text"), (created.Issue, created.Title, created.Body));
        Assert.Equal((1001, true, false), (commentId, edited, deletedOne));
        Assert.Equal(
            ["""{"title":"New","body":"Text"}""", """{"body":"Hello"}""", """{"body":"Hello again"}""", """{"body":"Anyone?"}"""],
            api.Requests.Skip(1).Select(sent => sent.Body));
    }

    [Fact]
    public async Task An_issue_that_is_gone_is_null_not_an_error()
    {
        api.On(HttpMethod.Get, "repositories/1296269/issues/42", "{}", HttpStatusCode.Gone);

        Assert.Null(await client.FindIssueAsync(Issue, Ct));
        Assert.Null(await client.FindIssueAsync(Issue with { Number = 404 }, Ct));   // nothing set up: the stand-in answers 404
    }

    [Fact]
    public async Task Listing_issues_asks_for_one_page_and_reads_the_last_page_from_the_link_header()
    {
        api.On(HttpMethod.Get, "repositories/1296269/issues", """[ { "number": 1, "title": "One", "state": "open" }, { "number": 2, "title": "A pull request", "state": "open", "pull_request": {} } ]""")
            .Headers.Add("Link", """<https://api.github.com/repositories/1296269/issues?page=3>; rel="next", <https://api.github.com/repositories/1296269/issues?per_page=100&page=7>; rel="last" """);

        IssuePage backfill = await client.ListIssuesAsync(Issue.Repo, new IssueQuery(Page: 2, Label: "good first issue", UpdatedSince: null), Ct);
        await client.ListIssuesAsync(Issue.Repo, new IssueQuery(Page: 1, Label: null, UpdatedSince: Now.AddHours(-25)), Ct);

        Assert.Equal((7, 2, true), (backfill.LastPage, backfill.Issues.Count, backfill.Issues[1].IsPullRequest));
        Assert.Equal("?per_page=100&page=2&sort=updated&direction=asc&state=open&labels=good%20first%20issue", api.Requests[^2].Query);
        Assert.Equal("?per_page=100&page=1&sort=updated&direction=asc&state=all&since=2026-11-15T08%3A30%3A00Z", api.Requests[^1].Query);
    }

    [Fact]
    public async Task The_check_run_is_created_for_the_commit_and_later_updated_by_its_id()
    {
        api.On(HttpMethod.Post, "repositories/1296269/check-runs", """{ "id": 9 }""");
        api.On(HttpMethod.Patch, "repositories/1296269/check-runs/9", """{ "id": 9 }""");

        long created = await client.SaveCheckRunAsync(Issue.Repo, checkRunId: null, new CheckRun("abc123", Passed: false, "- [ ] Code reviewed"), Ct);
        long updated = await client.SaveCheckRunAsync(Issue.Repo, created, new CheckRun("abc123", Passed: true, "- [x] Code reviewed"), Ct);

        Assert.Equal((9, 9), (created, updated));
        Assert.Equal(
            """{"name":"Kanban done rule","status":"completed","conclusion":"failure","output":{"title":"Done rule not met yet","summary":"- [ ] Code reviewed"},"head_sha":"abc123"}""",
            api.Requests[^2].Body);
        Assert.Equal(
            """{"name":"Kanban done rule","status":"completed","conclusion":"success","output":{"title":"Done rule met","summary":"- [x] Code reviewed"}}""",
            api.Requests[^1].Body);
    }

    [Fact]
    public async Task Finding_a_repository_asks_as_the_app_which_installation_reaches_it()
    {
        api.On(HttpMethod.Get, "repos/octocat/Hello-World/installation", """{ "id": 5 }""");
        api.On(HttpMethod.Get, "repos/octocat/Hello-World", """{ "id": 1296269, "full_name": "octocat/Hello-World" }""");

        RepositoryInfo? found = await client.FindRepositoryAsync("octocat/Hello-World", Ct);
        RepositoryInfo? notInstalled = await client.FindRepositoryAsync("octocat/private", Ct);

        Assert.Equal(new RepositoryInfo(new RepoRef(5, 1296269), "octocat/Hello-World"), found);
        Assert.Null(notInstalled);
        Assert.Equal(3, api.Requests[0].BearerToken.Split('.').Length);          // asked as the app
        Assert.Equal("ghs_installation", api.Requests[2].BearerToken);           // then as the installation
    }

    [Fact]
    public async Task A_rate_limit_becomes_the_time_to_come_back()
    {
        api.On(HttpMethod.Get, "repositories/1296269/issues/1", "{}", HttpStatusCode.TooManyRequests).Headers.Add("Retry-After", "30");
        HttpResponseMessage exhausted = api.On(HttpMethod.Get, "repositories/1296269/issues/2", "{}", HttpStatusCode.Forbidden);
        exhausted.Headers.Add("x-ratelimit-remaining", "0");
        exhausted.Headers.Add("x-ratelimit-reset", Now.AddMinutes(12).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        api.On(HttpMethod.Get, "repositories/1296269/issues/3", "{}", HttpStatusCode.Forbidden);

        var secondary = await Assert.ThrowsAsync<GitHubRateLimitedException>(() => client.FindIssueAsync(Issue with { Number = 1 }, Ct));
        var primary = await Assert.ThrowsAsync<GitHubRateLimitedException>(() => client.FindIssueAsync(Issue with { Number = 2 }, Ct));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.FindIssueAsync(Issue with { Number = 3 }, Ct));   // forbidden for another reason

        Assert.Equal((Now.AddSeconds(30), Now.AddMinutes(12)), (secondary.RetryAt, primary.RetryAt));
    }

    /// <summary>Signs with a key held in memory, where Azure signs inside Key Vault.</summary>
    private sealed class LocalSigner(RSA key) : IAppJwtSigner
    {
        public Task<byte[]> SignAsync(byte[] data, CancellationToken cancellationToken) =>
            Task.FromResult(key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
