using System.Collections.Concurrent;
using KanbanHub.Domain.Agents;

namespace KanbanHub.Agents;

/// <summary>What a person's token was used for: one assignment sent to an agent.</summary>
public sealed record class SentAssignment(string UserToken, AgentAssignment Assignment);

/// <summary>
/// Stands in for GitHub's agents on a development machine and in tests: it remembers what it was asked instead
/// of starting anything. The pull request an agent would open is played by hand, as a webhook delivery.
/// </summary>
public sealed class InMemoryAgentDispatcher : IAgentDispatcher
{
    private readonly ConcurrentQueue<SentAssignment> _sent = new();

    public IReadOnlyList<SentAssignment> Sent => [.. _sent];

    /// <summary>The logins the repository offers as assignees. Empty plays "Copilot isn't enabled here".</summary>
    public IReadOnlyList<string> Assignable { get; set; } = ["copilot-swe-agent", "octocat"];

    /// <summary>When set, the next dispatches are refused with this message, as GitHub would.</summary>
    public string? Refusal { get; set; }

    /// <summary>Plays an outage: GitHub can't be reached at all.</summary>
    public bool IsUnreachable { get; set; }

    public Task DispatchAsync(string userToken, AgentAssignment assignment, CancellationToken cancellationToken)
    {
        if (IsUnreachable) throw new HttpRequestException("GitHub can't be reached.");
        if (Refusal is { } refusal) throw new AgentDispatchException(refusal);

        _sent.Enqueue(new SentAssignment(userToken, assignment));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAssignableAsync(string userToken, string repository, CancellationToken cancellationToken) =>
        Task.FromResult(Assignable);
}
