using Azure.Messaging.ServiceBus;
using KanbanHub.Domain.Ai;

namespace KanbanHub.Messaging;

/// <summary>
/// Queues background AI work on <c>ai-jobs</c> (section 13.1), so an AI backlog never slows the API or the sync.
/// A message is the board's id with the feature as its subject.
/// </summary>
public sealed class ServiceBusAiJobInbox(ServiceBusSender sender) : IAiJobInbox
{
    public const string Queue = "ai-jobs";

    public Task EnqueueAsync(AiJob job, CancellationToken cancellationToken) =>
        sender.SendMessageAsync(
            new ServiceBusMessage(BinaryData.FromString(job.BoardId.ToString())) { Subject = job.Feature.ToString() },
            cancellationToken);

    /// <summary>The job a message carries, or null for a message this version doesn't understand.</summary>
    public static AiJob? Read(ServiceBusReceivedMessage message) =>
        Guid.TryParse(message.Body.ToString(), out Guid boardId) && Enum.TryParse(message.Subject, out AiFeature feature)
            ? new AiJob(boardId, feature)
            : null;
}
