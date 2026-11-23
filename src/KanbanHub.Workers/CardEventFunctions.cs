using Azure.Messaging.ServiceBus;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Notifications;
using KanbanHub.Messaging;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Workers;

/// <summary>
/// Consumes the <c>card-events</c> topic. Each consumer has its own subscription, so one failing
/// doesn't make the others repeat their work; Service Bus retries and dead-letters per subscription.
/// </summary>
public sealed class CardEventFunctions(NotificationGenerator notifications, DuplicateFinder duplicates)
{
    public const string NotificationsSubscription = "notifications";
    public const string DuplicatesSubscription = "duplicates";

    /// <summary>New and edited cards are compared with the rest of their board (section 11.1, feature 8).</summary>
    [Function("FindDuplicateCards")]
    public Task FindDuplicates(
        [ServiceBusTrigger(ServiceBusEventPublisher.Topic, DuplicatesSubscription, Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken) =>
        duplicates.HandleAsync(ServiceBusEventPublisher.Read(message), cancellationToken);

    [Function("NotifyAboutCardEvent")]
    public Task Notify(
        [ServiceBusTrigger(ServiceBusEventPublisher.Topic, NotificationsSubscription, Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken) =>
        notifications.HandleAsync(ServiceBusEventPublisher.Read(message), cancellationToken);
}
