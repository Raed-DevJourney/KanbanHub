using KanbanHub.Domain.Boards;

namespace KanbanHub.Domain.Agents;

/// <summary>
/// The feature flag for agent dispatch. GitHub's APIs for it are in public preview (section 10),
/// so an environment can switch the whole feature off without a deployment.
/// </summary>
public sealed record class AgentSettings(bool IsEnabled);

/// <summary>A coding agent an issue can be assigned to, and the logins GitHub shows it under.</summary>
public sealed record class Agent(string Key, string Name, string AssigneeLogin)
{
    /// <summary>Other spellings of the agent's login: GitHub's APIs and webhooks don't all use the same one.</summary>
    public IReadOnlyList<string> AlsoKnownAs { get; init; } = [];

    /// <summary>True when a GitHub login is this agent's.</summary>
    public bool Answers(string login) =>
        AlsoKnownAs.Append(AssigneeLogin).Contains(login, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The agents KanbanHub can dispatch to. Claude and Codex join when GitHub's support for them leaves preview.</summary>
public static class AgentCatalog
{
    public static Agent Copilot { get; } = new("copilot", "GitHub Copilot", "copilot-swe-agent[bot]")
    {
        AlsoKnownAs = ["copilot-swe-agent", "Copilot"]
    };

    public static IReadOnlyList<Agent> All { get; } = [Copilot];

    public static Agent? Find(string key) => All.FirstOrDefault(agent => string.Equals(agent.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>How far an agent session got. Stored in <c>kb.AgentDispatch.State</c>, so never renumber.</summary>
public enum DispatchState : byte
{
    /// <summary>Sent to the agent, no pull request yet: the card shows "Agent working".</summary>
    Working = 1,

    /// <summary>The agent's pull request is open: people review it.</summary>
    InReview = 2,

    Merged = 3,

    /// <summary>The pull request was closed without merging.</summary>
    Closed = 4,

    /// <summary>GitHub refused the assignment.</summary>
    Failed = 5,

    /// <summary>A person ended it, here or by unassigning the agent on GitHub.</summary>
    Ended = 6
}

public static class DispatchStates
{
    /// <summary>Open sessions occupy an agent slot on their board: someone still has to review them.</summary>
    public static bool IsOpen(this DispatchState state) => state is DispatchState.Working or DispatchState.InReview;
}

/// <summary>An agent session on a card.</summary>
public sealed record class AgentSession
{
    public required Guid Id { get; init; }

    public required Guid CardId { get; init; }

    public required string Agent { get; init; }

    public required string TargetRepository { get; init; }

    public required string BaseBranch { get; init; }

    public string? Model { get; init; }

    public DispatchState State { get; init; } = DispatchState.Working;

    public int? PullRequestNumber { get; init; }

    public required Guid DispatchedBy { get; init; }

    public required DateTimeOffset DispatchedAt { get; init; }
}

/// <summary>How many agent sessions a board has open, against how many it allows (section 10.2).</summary>
public readonly record struct AgentCapacity(int Open, int Limit)
{
    public bool IsFull => Open >= Limit;
}

/// <summary>What a person submits to send a card to an agent.</summary>
public sealed record class DispatchCommand(Guid CardId, string Agent, string BaseBranch, string? Instructions)
{
    public string? Model { get; init; }

    public string? CustomAgent { get; init; }
}

/// <summary>What GitHub is asked: assign the issue to the agent, with where and how it should work (section 10.4).</summary>
public sealed record class AgentAssignment(string Repository, int IssueNumber, string AssigneeLogin)
{
    public required string TargetRepository { get; init; }

    public required string BaseBranch { get; init; }

    public required string Instructions { get; init; }

    public string? CustomAgent { get; init; }

    public string? Model { get; init; }
}

/// <summary>GitHub refused to start the agent, for example because it isn't enabled for the repository or the user.</summary>
public sealed class AgentDispatchException(string message) : Exception(message);

/// <summary>
/// The adapter in front of GitHub's agent APIs. They are preview APIs, so everything that knows their shape
/// lives behind this port.
/// </summary>
public interface IAgentDispatcher
{
    /// <summary>Assigns the issue to the agent. Needs the person's own token: GitHub refuses installation tokens here.</summary>
    /// <exception cref="AgentDispatchException">GitHub refused.</exception>
    Task DispatchAsync(string userToken, AgentAssignment assignment, CancellationToken cancellationToken);

    /// <summary>The logins GitHub says can be assigned in the repository, agents included.</summary>
    Task<IReadOnlyList<string>> ListAssignableAsync(string userToken, string repository, CancellationToken cancellationToken);
}

public enum StartResult
{
    Started = 0,
    BoardAtLimit = 1,
    CardBusy = 2
}

/// <summary>Where a session moved to; the pull request number is kept once it is known.</summary>
public readonly record struct SessionProgress(DispatchState State, int? PullRequestNumber = null);

/// <summary>Storage port for agent sessions.</summary>
public interface IAgentSessionStore
{
    Task<AgentSession?> FindOpenAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>The card's most recent session, open or not.</summary>
    Task<AgentSession?> FindLatestAsync(Guid cardId, CancellationToken cancellationToken);

    Task<AgentCapacity> FindCapacityAsync(Guid boardId, CancellationToken cancellationToken);

    /// <summary>Takes an agent slot for the card, atomically: the board's limit holds under concurrent starts.</summary>
    Task<StartResult> StartAsync(AgentSession session, byte[] instructionsHash, Stamp stamp, CancellationToken cancellationToken);

    Task UpdateAsync(Guid sessionId, SessionProgress progress, DateTimeOffset now, CancellationToken cancellationToken);
}
