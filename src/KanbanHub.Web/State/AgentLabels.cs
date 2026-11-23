using KanbanHub.Contracts;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.GitHub;

namespace KanbanHub.Web.State;

/// <summary>The words the app uses for agent sessions and pull requests.</summary>
public static class AgentLabels
{
    public static string NameOf(string agentKey) => AgentCatalog.Find(agentKey)?.Name ?? agentKey;

    /// <summary>The badge on a card with an open session: the agent works, or its pull request waits for review.</summary>
    public static string BadgeOf(CardAgentDto agent) =>
        agent is { State: DispatchState.InReview, PullRequestNumber: { } number } ? $"Agent PR #{number}" : "Agent working";

    public static string Describe(AgentSessionDto session)
    {
        string name = NameOf(session.Agent);
        string pullRequest = $"pull request #{session.PullRequestNumber}";

        return session.State switch
        {
            DispatchState.Working => $"{name} is working on this card, from branch {session.BaseBranch}.",
            DispatchState.InReview => $"{name} opened {pullRequest}. It waits for review.",
            DispatchState.Merged => $"{name}'s {pullRequest} was merged.",
            DispatchState.Closed => $"{name}'s {pullRequest} was closed without merging.",
            DispatchState.Failed => $"{name} could not be started.",
            _ => $"The session with {name} was ended."
        };
    }

    public static string StateOf(PullRequestDto pullRequest) => pullRequest.State switch
    {
        PullRequestState.Merged => "Merged",
        PullRequestState.Closed => "Closed",
        _ => pullRequest.IsDraft ? "Draft" : "Open"
    };
}
