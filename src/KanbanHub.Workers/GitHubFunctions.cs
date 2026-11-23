using Azure.Messaging.ServiceBus;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.GitHub;
using KanbanHub.Messaging;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Workers;

/// <summary>
/// The inbound half of the GitHub sync (section 9.2): takes webhook deliveries off the session queue, one issue
/// at a time and in order. Failures are retried by Service Bus and dead-lettered after the queue's delivery count.
/// </summary>
public sealed class GitHubInbound(IGitHubEventParser parser, InboundProcessor processor, OutboxDispatcher outbox, ScheduledRetry retry)
{
    [Function("ProcessGitHubDelivery")]
    public async Task Process(
        [ServiceBusTrigger(ServiceBusGitHubInbox.Queue, Connection = "ServiceBus", IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await processor.HandleAsync(parser.Parse(ServiceBusGitHubInbox.Read(message)), cancellationToken);
        }
        catch (GitHubRateLimitedException limited)
        {
            await retry.InboundAsync(message, limited.RetryAt, cancellationToken);
        }

        // What the delivery changed reaches the people watching the board now, not at the next sweep.
        await outbox.DispatchAsync(minimumAge: TimeSpan.Zero, cancellationToken);
    }
}

/// <summary>
/// The outbound half (section 9.3): the <c>github</c> subscription of the card-events topic forwards to this
/// session queue, so each card's changes are written to its issue in order. A repository that was just linked
/// is backfilled here too. When GitHub says to wait, the message comes back at the time it names; what was
/// done before that is not done again, because the sync only sends what differs.
/// </summary>
public sealed class GitHubOutbound(GitHubWriter writer, BackfillOnLink backfill, ScheduledRetry retry)
{
    [Function("WriteToGitHub")]
    public async Task Write(
        [ServiceBusTrigger(ScheduledRetry.OutboundQueue, Connection = "ServiceBus", IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        BoardEvent boardEvent = ServiceBusEventPublisher.Read(message);
        try
        {
            await writer.HandleAsync(boardEvent, cancellationToken);
            await backfill.HandleAsync(boardEvent, cancellationToken);
        }
        catch (GitHubRateLimitedException limited)
        {
            await retry.OutboundAsync(message, limited.RetryAt, cancellationToken);
        }
    }
}

/// <summary>
/// The nightly reconciler (section 9.5): lists the issues of every linked repository that changed in the last
/// day and applies what a missed webhook would have. The window overlaps a little so a late run loses nothing.
/// </summary>
public sealed class NightlyReconcile(IRepoLinkStore repositories, Backfill backfill, TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(25);

    [Function("ReconcileGitHub")]
    public async Task Reconcile([TimerTrigger("0 30 2 * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        DateTimeOffset since = clock.GetUtcNow() - Window;
        foreach (RepoLink repository in await repositories.ListAllAsync(cancellationToken))
            await backfill.SyncAllAsync(repository, since, cancellationToken);
    }
}
