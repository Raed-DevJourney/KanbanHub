using System.Net;
using System.Text.Json;
using System.Web;
using KanbanHub.Agents;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Domain.Agents;
using KanbanHub.GitHub;

namespace KanbanHub.Api.Tests.Agents;

/// <summary>
/// The agent adapter against responses in the shape GitHub documents (specification sections 10.4 and 19.1).
/// These APIs are in public preview: when GitHub changes them, these are the tests to update first.
/// </summary>
public sealed class CopilotDispatcherTests : IDisposable
{
    private const string UserToken = "ghu_users_own_token";

    private static readonly AgentAssignment Assignment = new("contoso/checkout", 42, "copilot-swe-agent[bot]")
    {
        TargetRepository = "contoso/checkout",
        BaseBranch = "main",
        Instructions = "Keep the public API stable.\n\"Quoted\" text survives.",
        Model = "gpt-5.2-codex"
    };

    private readonly FakeGitHubApi api = new();
    private readonly CopilotDispatcher dispatcher;

    public CopilotDispatcherTests() => dispatcher = new CopilotDispatcher(api);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => api.Dispose();

    [Fact]
    public async Task Dispatching_assigns_the_issue_to_the_agent_with_an_agent_assignment_as_the_user()
    {
        api.On(HttpMethod.Post, "repos/contoso/checkout/issues/42/assignees",
            """{ "number": 42, "assignees": [ { "login": "Copilot", "type": "Bot" } ] }""", HttpStatusCode.Created);

        await dispatcher.DispatchAsync(UserToken, Assignment, Ct);

        SentRequest request = Assert.Single(api.Requests);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        JsonElement options = body.RootElement.GetProperty("agent_assignment");
        Assert.Equal((HttpMethod.Post, "api.github.com", UserToken), (request.Method, request.Host, request.BearerToken));
        Assert.Equal(["copilot-swe-agent[bot]"], body.RootElement.GetProperty("assignees").EnumerateArray().Select(login => login.GetString()));
        Assert.Equal(["target_repo", "base_branch", "custom_instructions", "custom_agent", "model"], options.EnumerateObject().Select(member => member.Name));
        Assert.Equal(("contoso/checkout", "main", Assignment.Instructions, "", "gpt-5.2-codex"),
            (Text(options, "target_repo"), Text(options, "base_branch"), Text(options, "custom_instructions"), Text(options, "custom_agent"), Text(options, "model")));
    }

    private static string? Text(JsonElement json, string member) => json.GetProperty(member).GetString();

    [Fact]
    public async Task The_request_is_json_with_exactly_the_two_documented_members()
    {
        api.On(HttpMethod.Post, "repos/contoso/checkout/issues/42/assignees", "{}", HttpStatusCode.Created);

        await dispatcher.DispatchAsync(UserToken, Assignment with { Instructions = "Do it." }, Ct);

        Assert.Equal(
            """{"assignees":["copilot-swe-agent[bot]"],"agent_assignment":{"target_repo":"contoso/checkout","base_branch":"main","custom_instructions":"Do it.","custom_agent":"","model":"gpt-5.2-codex"}}""",
            Assert.Single(api.Requests).Body);
    }

    [Fact]
    public async Task Options_left_out_are_sent_as_empty_strings_which_is_how_the_api_spells_the_default()
    {
        api.On(HttpMethod.Post, "repos/contoso/checkout/issues/42/assignees", "{}", HttpStatusCode.Created);

        await dispatcher.DispatchAsync(UserToken, Assignment with { Model = null, CustomAgent = null }, Ct);

        Assert.EndsWith("\"custom_agent\":\"\",\"model\":\"\"}}", api.Requests[0].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "Copilot cloud agent isn't enabled for this repository or user.")]
    [InlineData(HttpStatusCode.Unauthorized, "GitHub no longer accepts your connection. Connect GitHub again in your profile.")]
    [InlineData(HttpStatusCode.Forbidden, "GitHub won't let you assign issues in contoso/checkout. Check your access there, and that the KanbanHub app is authorized for it.")]
    [InlineData(HttpStatusCode.NotFound, "GitHub won't let you assign issues in contoso/checkout. Check your access there, and that the KanbanHub app is authorized for it.")]
    public async Task A_refusal_from_github_becomes_words_a_person_can_act_on(HttpStatusCode status, string expected)
    {
        api.On(HttpMethod.Post, "repos/contoso/checkout/issues/42/assignees", """{ "message": "Validation Failed" }""", status);

        var refused = await Assert.ThrowsAsync<AgentDispatchException>(() => dispatcher.DispatchAsync(UserToken, Assignment, Ct));

        Assert.Equal(expected, refused.Message);
    }

    [Fact]
    public async Task A_failure_on_githubs_side_is_not_mistaken_for_a_refusal()
    {
        api.On(HttpMethod.Post, "repos/contoso/checkout/issues/42/assignees", "{}", HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<HttpRequestException>(() => dispatcher.DispatchAsync(UserToken, Assignment, Ct));
    }

    [Fact]
    public async Task The_agents_on_offer_come_from_the_repositorys_suggested_actors()
    {
        api.On(HttpMethod.Post, "graphql", """
            {
              "data": {
                "repository": {
                  "suggestedActors": {
                    "nodes": [
                      { "login": "copilot-swe-agent", "__typename": "Bot", "id": "BOT_kgDOC9w8XQ" },
                      { "login": "octocat", "__typename": "User", "id": "MDQ6VXNlcjU4MzIzMQ==" }
                    ]
                  }
                }
              }
            }
            """);

        IReadOnlyList<string> logins = await dispatcher.ListAssignableAsync(UserToken, "contoso/checkout", Ct);

        SentRequest request = Assert.Single(api.Requests);
        Assert.Equal(["copilot-swe-agent", "octocat"], logins);
        Assert.Equal((UserToken, CopilotDispatcher.PreviewFeatures), (request.BearerToken, request.Headers["GraphQL-Features"]));
        Assert.Contains("suggestedActors(capabilities: [CAN_BE_ASSIGNED], first: 100)", request.Body, StringComparison.Ordinal);
        Assert.EndsWith("\"variables\":{\"owner\":\"contoso\",\"name\":\"checkout\"}}", request.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "data": { "repository": null }, "errors": [ { "type": "NOT_FOUND" } ] }""")]
    [InlineData("""{ "data": { "repository": { "suggestedActors": { "nodes": [] } } } }""")]
    [InlineData("""{ "errors": [ { "message": "Field 'suggestedActors' doesn't exist on type 'Repository'" } ] }""")]
    public async Task A_repository_the_person_cannot_see_or_an_answer_in_another_shape_offers_no_agents(string answer)
    {
        api.On(HttpMethod.Post, "graphql", answer);

        Assert.Empty(await dispatcher.ListAssignableAsync(UserToken, "contoso/secret", Ct));
    }
}

/// <summary>"Connect GitHub" against the answers GitHub's OAuth endpoints give for a GitHub App.</summary>
public sealed class GitHubUserAuthorizationTests : IDisposable
{
    // The example tokens from GitHub's documentation, in halves so secret scanners don't stop at them.
    private const string AccessToken = "ghu_" + "16C7e42F292c6912E7710c838347Ae178B4a";
    private const string RefreshToken = "ghr_" + "1B4a2e77838347a7E420ce178F2E7c6912E169246c34E1ccbF66C46812d16D5B1A9Dc86A1498";

    private const string Tokens = $$"""
        {
          "access_token": "{{AccessToken}}", "expires_in": 28800,
          "refresh_token": "{{RefreshToken}}",
          "refresh_token_expires_in": 15811200, "scope": "", "token_type": "bearer"
        }
        """;

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);

    private static readonly GitHubAppOptions Options = new()
    {
        ClientId = "Iv23liTestApp",
        ClientSecret = "not-a-real-secret",
        WebBaseUrl = new Uri("https://kanbanhub.test/")
    };

    private readonly FakeGitHubApi api = new();
    private readonly GitHubUserAuthorization authorization;

    public GitHubUserAuthorizationTests() => authorization = new GitHubUserAuthorization(api, Options, new FixedClock(Now));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => api.Dispose();

    [Fact]
    public void The_person_is_sent_to_github_with_the_apps_id_the_callback_page_and_the_state()
    {
        Uri url = authorization.AuthorizeUrl("a1b2c3");

        Assert.Equal(
            "https://github.com/login/oauth/authorize?client_id=Iv23liTestApp&redirect_uri=https%3A%2F%2Fkanbanhub.test%2Fgithub%2Fcallback&state=a1b2c3",
            url.AbsoluteUri);
    }

    [Fact]
    public async Task The_code_is_traded_for_tokens_and_the_tokens_say_who_the_person_is()
    {
        api.On(HttpMethod.Post, "login/oauth/access_token", Tokens);
        api.On(HttpMethod.Get, "user", """{ "login": "octocat", "id": 583231, "type": "User" }""");

        AuthorizedUser? authorized = await authorization.ExchangeAsync("the-code", Ct);

        Assert.NotNull(authorized);
        Assert.Equal((583231, "octocat"), (authorized.GitHubUserId, authorized.Login));
        Assert.Equal(Now.AddHours(8), authorized.Tokens.ExpiresAt);
        Assert.Equal(RefreshToken, authorized.Tokens.RefreshToken);

        var form = HttpUtility.ParseQueryString(api.Requests[0].Body);
        Assert.Equal(("github.com", "the-code", "Iv23liTestApp", "not-a-real-secret", "https://kanbanhub.test/github/callback"),
            (api.Requests[0].Host, form["code"], form["client_id"], form["client_secret"], form["redirect_uri"]));
        Assert.Equal(("api.github.com", authorized.Tokens.AccessToken), (api.Requests[1].Host, api.Requests[1].BearerToken));
    }

    /// <summary>GitHub answers 200 with an error member when it rejects a code or a refresh token.</summary>
    [Fact]
    public async Task A_rejected_code_or_refresh_token_is_null_not_an_exception()
    {
        api.On(HttpMethod.Post, "login/oauth/access_token",
            """{ "error": "bad_verification_code", "error_description": "The code passed is incorrect or expired.", "error_uri": "https://docs.github.com/" }""");

        Assert.Null(await authorization.ExchangeAsync("stale-code", Ct));
        Assert.Null(await authorization.RefreshAsync("ghr_revoked", Ct));
    }

    [Fact]
    public async Task Renewing_sends_the_refresh_token_and_returns_the_new_pair()
    {
        api.On(HttpMethod.Post, "login/oauth/access_token", Tokens);

        UserTokens? renewed = await authorization.RefreshAsync("ghr_previous", Ct);

        var form = HttpUtility.ParseQueryString(Assert.Single(api.Requests).Body);
        Assert.Equal(("refresh_token", "ghr_previous", "Iv23liTestApp"), (form["grant_type"], form["refresh_token"], form["client_id"]));
        Assert.Equal((AccessToken, Now.AddHours(8)), (renewed?.AccessToken, renewed?.ExpiresAt));
    }

    /// <summary>An app with token expiry switched off hands out no refresh token; storing nothing would fail later and silently.</summary>
    [Fact]
    public async Task An_app_that_does_not_expire_user_tokens_is_a_configuration_error_that_says_so()
    {
        api.On(HttpMethod.Post, "login/oauth/access_token", """{ "access_token": "ghu_forever", "scope": "", "token_type": "bearer" }""");

        var misconfigured = await Assert.ThrowsAsync<InvalidOperationException>(() => authorization.ExchangeAsync("the-code", Ct));

        Assert.Contains("Expire user authorization tokens", misconfigured.Message, StringComparison.Ordinal);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public class AgentStandInTests
{
    private static readonly AgentAssignment Assignment = new("contoso/checkout", 42, "copilot-swe-agent[bot]")
    {
        TargetRepository = "contoso/checkout",
        BaseBranch = "main",
        Instructions = "Do it."
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_stand_in_dispatcher_remembers_what_it_was_asked_and_can_play_a_refusal()
    {
        var dispatcher = new InMemoryAgentDispatcher();

        await dispatcher.DispatchAsync("token-1", Assignment, Ct);
        dispatcher.Refusal = "Not today.";
        var refused = await Assert.ThrowsAsync<AgentDispatchException>(() => dispatcher.DispatchAsync("token-2", Assignment, Ct));

        Assert.Equal(new SentAssignment("token-1", Assignment), Assert.Single(dispatcher.Sent));
        Assert.Equal("Not today.", refused.Message);
        Assert.Contains("copilot-swe-agent", await dispatcher.ListAssignableAsync("token-1", "contoso/checkout", Ct));
    }

    [Fact]
    public async Task The_stand_in_authorization_approves_at_once_and_rotates_its_tokens()
    {
        var authorization = new InMemoryGitHubAuthorization(new Uri("https://kanbanhub.test/github/callback"), TimeProvider.System);

        Uri back = authorization.AuthorizeUrl("s1");
        AuthorizedUser? authorized = await authorization.ExchangeAsync(InMemoryGitHubAuthorization.Code, Ct);
        UserTokens? renewed = await authorization.RefreshAsync(authorized!.Tokens.RefreshToken, Ct);
        authorization.IsRevoked = true;

        Assert.Equal("https://kanbanhub.test/github/callback?code=local-code&state=s1", back.AbsoluteUri);
        Assert.Equal("octocat", authorized.Login);
        Assert.NotEqual(authorized.Tokens, renewed);
        Assert.Null(await authorization.ExchangeAsync("another-code", Ct));
        Assert.Null(await authorization.RefreshAsync(renewed!.RefreshToken, Ct));
    }

    [Fact]
    public async Task The_local_token_protector_gives_back_what_it_was_given()
    {
        var protector = new LocalTokenProtector();

        Assert.Equal("local-refresh-1", await protector.UnprotectAsync(await protector.ProtectAsync("local-refresh-1", Ct), Ct));
    }
}
