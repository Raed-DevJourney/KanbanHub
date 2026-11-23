using System.Globalization;
using Azure.Messaging.ServiceBus;
using KanbanHub.Domain.GitHub;

namespace KanbanHub.Messaging;

/// <summary>
/// Queues verified webhook deliveries on <c>github-inbound</c> (section 9.2): the delivery id lets Service Bus
/// drop duplicates, and one session per issue keeps that issue's events in order.
/// </summary>
public sealed class ServiceBusGitHubInbox(ServiceBusSender sender, IGitHubEventParser parser) : IGitHubInbox
{
    public const string Queue = "github-inbound";

    public Task EnqueueAsync(RawDelivery delivery, CancellationToken cancellationToken) =>
        sender.SendMessageAsync(
            new ServiceBusMessage(BinaryData.FromString(delivery.Payload))
            {
                MessageId = delivery.DeliveryId,
                SessionId = parser.SessionKeyOf(delivery),
                Subject = delivery.EventName,
                ContentType = "application/json",
                ApplicationProperties = { [ScheduledRetry.OriginalId] = delivery.DeliveryId }
            },
            cancellationToken);

    /// <summary>The delivery a message carries. A retried message has a new message id but the delivery's own id.</summary>
    public static RawDelivery Read(ServiceBusReceivedMessage message) => new(
        message.ApplicationProperties.TryGetValue(ScheduledRetry.OriginalId, out object? original) ? $"{original}" : message.MessageId,
        message.Subject, message.Body.ToString());
}

/// <summary>
/// Puts a message back on its queue for the moment GitHub's rate limit ends (section 9.6), instead of
/// burning delivery attempts until it dead-letters.
/// </summary>
public sealed class ScheduledRetry(ServiceBusClient client) : IAsyncDisposable
{
    public const string OutboundQueue = "github-outbound";
    public const string OriginalId = "kanbanhub-original-id";

    private readonly ServiceBusSender inbound = client.CreateSender(ServiceBusGitHubInbox.Queue);
    private readonly ServiceBusSender outbound = client.CreateSender(OutboundQueue);

    public Task InboundAsync(ServiceBusReceivedMessage message, DateTimeOffset at, CancellationToken cancellationToken) =>
        inbound.ScheduleMessageAsync(Copy(message, at), at, cancellationToken);

    public Task OutboundAsync(ServiceBusReceivedMessage message, DateTimeOffset at, CancellationToken cancellationToken) =>
        outbound.ScheduleMessageAsync(Copy(message, at), at, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await inbound.DisposeAsync();
        await outbound.DisposeAsync();
    }

    /// <summary>The copy needs its own message id, or duplicate detection would drop it as the message it retries.</summary>
    private static ServiceBusMessage Copy(ServiceBusReceivedMessage message, DateTimeOffset at) => new(message)
    {
        MessageId = string.Create(CultureInfo.InvariantCulture, $"{message.MessageId}/retry-{at.ToUnixTimeSeconds()}")
    };
}
