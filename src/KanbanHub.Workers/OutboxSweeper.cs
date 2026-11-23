using KanbanHub.Domain.Events;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Workers;

/// <summary>
/// Publishes events the API could not publish right after their commit (specification section 9.3).
/// Younger events are left alone: the request that wrote them is still sending them.
/// </summary>
public sealed class OutboxSweeper(OutboxDispatcher dispatcher)
{
    private static readonly TimeSpan LeftBehindAfter = TimeSpan.FromSeconds(30);

    [Function("SweepOutbox")]
    public Task Sweep([TimerTrigger("0 * * * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        dispatcher.DispatchAsync(LeftBehindAfter, cancellationToken);
}
