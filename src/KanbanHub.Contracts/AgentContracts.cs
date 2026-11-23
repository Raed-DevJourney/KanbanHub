using KanbanHub.Domain.Agents;
using KanbanHub.Domain.GitHub;

namespace KanbanHub.Contracts;

/// <summary>The agent session open on a card: what the "Agent working" badge is drawn from.</summary>
public sealed record CardAgentDto(string Agent, DispatchState State, int? PullRequestNumber);

/// <summary>A pull request that closes the card's issue.</summary>
public sealed record PullRequestDto(int Number, PullRequestState State, bool IsDraft, string? Repository)
{
    public string? Url => Repository is null ? null : $"https://github.com/{Repository}/pull/{Number}";
}

/// <summary><c>GET /github/connection</c>: the caller's connected GitHub account, when they have one.</summary>
public sealed record GitHubConnectionDto(string? Login)
{
    public bool IsConnected => Login is not null;
}

/// <summary><c>GET /github/connect</c>: where to send the browser so the person can approve the GitHub App.</summary>
public sealed record ConnectGitHubResponse(string AuthorizeUrl);

/// <summary>An agent a card can be sent to.</summary>
public sealed record AgentDto(string Key, string Name);

/// <summary>An agent session on a card, open or over.</summary>
public sealed record AgentSessionDto
{
    public required string Agent { get; init; }

    public required DispatchState State { get; init; }

    public int? PullRequestNumber { get; init; }

    public required string Repository { get; init; }

    public required string BaseBranch { get; init; }

    public string? Model { get; init; }

    public required Guid DispatchedBy { get; init; }

    public required DateTime DispatchedAtUtc { get; init; }

    public string? PullRequestUrl =>
        PullRequestNumber is { } number ? $"https://github.com/{Repository}/pull/{number}" : null;
}

/// <summary>
/// <c>GET /cards/{id}/agent-dispatch</c>: what the "Send to agent" form shows, and why sending isn't possible
/// right now when it isn't.
/// </summary>
public sealed record AgentDeskDto
{
    /// <summary>False when agent dispatch is switched off for this environment (the feature flag).</summary>
    public required bool IsEnabled { get; init; }

    public required int OpenSessions { get; init; }

    public required int SessionLimit { get; init; }

    /// <summary>The caller's connected GitHub login; agents run in their name and on their Copilot plan.</summary>
    public string? GitHubLogin { get; init; }

    public IReadOnlyList<AgentDto> Agents { get; init; } = [];

    public string? Repository { get; init; }

    public AgentSessionDto? Latest { get; init; }

    public string? WhyNot { get; init; }
}

/// <summary>The body of <c>POST /cards/{id}/agent-dispatch</c>. The agent works in the repository of the card's issue.</summary>
public sealed record DispatchAgentRequest(
    string Agent, string BaseBranch, string? Instructions = null, string? Model = null, string? CustomAgent = null);
