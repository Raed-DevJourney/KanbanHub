using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KanbanHub.Domain.Agents;
using KanbanHub.GitHub;

namespace KanbanHub.Agents;

/// <summary>
/// Starts an agent the way GitHub documents it (section 10.4): the issue is assigned to the agent's login through
/// the issues REST API, with an <c>agent_assignment</c> saying where and how it should work. Claude and Codex use
/// the same call with their own logins. Every request carries the person's own token: GitHub refuses
/// installation tokens here.
/// </summary>
public sealed class CopilotDispatcher(IHttpClientFactory http) : IAgentDispatcher
{
    /// <summary>GitHub's documentation asks for this header while agent assignment is in preview.</summary>
    public const string PreviewFeatures = "issues_copilot_assignment_api_support,coding_agent_model_selection";

    private const string AssignableQuery = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            suggestedActors(capabilities: [CAN_BE_ASSIGNED], first: 100) { nodes { login } }
          }
        }
        """;

    public async Task DispatchAsync(string userToken, AgentAssignment assignment, CancellationToken cancellationToken)
    {
        string url = $"repos/{assignment.Repository}/issues/{assignment.IssueNumber}/assignees";
        using HttpResponseMessage response = await SendAsync(userToken, url, BodyOf(assignment), cancellationToken);

        if (ExplainRefusal(response.StatusCode, assignment.Repository) is { } refusal) throw new AgentDispatchException(refusal);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Asks which actors the repository suggests as assignees. An agent is among them only when it is enabled for
    /// the repository and for the person asking, so this is what decides which agents the dialog offers.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAssignableAsync(string userToken, string repository, CancellationToken cancellationToken)
    {
        string[] parts = repository.Split('/', 2);
        var variables = new JsonObject { ["owner"] = parts[0], ["name"] = parts.ElementAtOrDefault(1) ?? "" };
        var query = new JsonObject { ["query"] = AssignableQuery, ["variables"] = variables };

        using HttpResponseMessage response = await SendAsync(userToken, "graphql", query, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument answer = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return LoginsOf(answer.RootElement);
    }

    private async Task<HttpResponseMessage> SendAsync(string userToken, string url, JsonObject body, CancellationToken cancellationToken)
    {
        using HttpClient client = http.CreateClient(GitHubRestClient.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        request.Headers.Add("GraphQL-Features", PreviewFeatures);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>The preview API wants every option present; an empty string means "the default".</summary>
    private static JsonObject BodyOf(AgentAssignment assignment) => new()
    {
        ["assignees"] = new JsonArray(assignment.AssigneeLogin),
        ["agent_assignment"] = new JsonObject
        {
            ["target_repo"] = assignment.TargetRepository,
            ["base_branch"] = assignment.BaseBranch,
            ["custom_instructions"] = assignment.Instructions,
            ["custom_agent"] = assignment.CustomAgent ?? "",
            ["model"] = assignment.Model ?? ""
        }
    };

    /// <summary>A repository the person can't see comes back as null; that offers no agents rather than failing.</summary>
    private static IReadOnlyList<string> LoginsOf(JsonElement answer) =>
        answer.TryGetProperty("data", out JsonElement data)
        && data.TryGetProperty("repository", out JsonElement repository) && repository.ValueKind is JsonValueKind.Object
        && repository.TryGetProperty("suggestedActors", out JsonElement actors)
        && actors.TryGetProperty("nodes", out JsonElement nodes) && nodes.ValueKind is JsonValueKind.Array
            ? [.. nodes.EnumerateArray().Select(LoginOf).OfType<string>()]
            : [];

    private static string? LoginOf(JsonElement actor) =>
        actor.ValueKind is JsonValueKind.Object && actor.TryGetProperty("login", out JsonElement login) ? login.GetString() : null;

    /// <summary>The answers that mean "GitHub said no", in words a person can act on. Anything else is a failure to reach GitHub.</summary>
    private static string? ExplainRefusal(HttpStatusCode status, string repository) => status switch
    {
        HttpStatusCode.UnprocessableEntity => "Copilot cloud agent isn't enabled for this repository or user.",
        HttpStatusCode.Unauthorized => "GitHub no longer accepts your connection. Connect GitHub again in your profile.",
        HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
            $"GitHub won't let you assign issues in {repository}. Check your access there, and that the KanbanHub app is authorized for it.",
        _ => null
    };
}
