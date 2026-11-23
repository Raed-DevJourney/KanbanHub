using System.Globalization;
using Azure;
using Azure.Communication.Email;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Notifications;
using Microsoft.Azure.SignalR;
using Microsoft.Azure.SignalR.Management;
using DomainEmail = KanbanHub.Domain.Notifications.EmailMessage;

namespace KanbanHub.Messaging;

/// <summary>Real-time through Azure SignalR Service in serverless mode, authenticated with the app's identity.</summary>
public sealed class SignalRServiceGateway(ServiceManager manager) : IRealtimeGateway
{
    public const string HubName = "board";

    private readonly Lazy<Task<ServiceHubContext>> hub = new(() => manager.CreateHubContextAsync(HubName, CancellationToken.None));

    public static SignalRServiceGateway Create(Uri serviceUri, TokenCredential credential) => new(
        new ServiceManagerBuilder()
            .WithOptions(options => options.ServiceEndpoints = [new ServiceEndpoint(serviceUri, credential)])
            .BuildServiceManager());

    /// <summary>The connection is issued for this user id, so messages to a user reach only that user.</summary>
    public async Task<RealtimeConnection> NegotiateAsync(Guid userId, CancellationToken cancellationToken)
    {
        ServiceHubContext context = await hub.Value;
        var negotiation = await context.NegotiateAsync(new NegotiationOptions { UserId = userId.ToString() }, cancellationToken);
        return new RealtimeConnection(negotiation.Url ?? "", negotiation.AccessToken);
    }

    public async Task JoinBoardAsync(string connectionId, Guid boardId, CancellationToken cancellationToken) =>
        await (await hub.Value).Groups.AddToGroupAsync(connectionId, IRealtimeGateway.GroupOf(boardId), cancellationToken);

    public async Task BoardChangedAsync(BoardChanged change, CancellationToken cancellationToken) =>
        await (await hub.Value).Clients.Group(IRealtimeGateway.GroupOf(change.BoardId))
            .SendCoreAsync(IRealtimeGateway.BoardChangedMethod, [change.BoardId, change.EventType, change.CardId], cancellationToken);

    public async Task InboxChangedAsync(Guid userId, CancellationToken cancellationToken) =>
        await (await hub.Value).Clients.User(userId.ToString())
            .SendCoreAsync(IRealtimeGateway.InboxChangedMethod, [], cancellationToken);
}

/// <summary>Publishes board events to the <c>card-events</c> topic; the outbox id makes duplicate detection work.</summary>
public sealed class ServiceBusEventPublisher(ServiceBusSender sender) : IBoardEventPublisher
{
    public const string Topic = "card-events";

    /// <summary>The event a message from the topic carries.</summary>
    public static BoardEvent Read(ServiceBusReceivedMessage message) =>
        message.Body.ToObjectFromJson<BoardEvent>()
        ?? throw new InvalidOperationException($"Message {message.MessageId} carries no board event.");

    public Task PublishAsync(BoardEvent boardEvent, CancellationToken cancellationToken) =>
        sender.SendMessageAsync(
            new ServiceBusMessage(BinaryData.FromObjectAsJson(boardEvent))
            {
                MessageId = boardEvent.Id.ToString(CultureInfo.InvariantCulture),
                SessionId = boardEvent.CardId.ToString(),   // the GitHub writer's queue handles one card at a time, in order
                Subject = boardEvent.Type,
                ContentType = "application/json"
            },
            cancellationToken);
}

/// <summary>Email through Azure Communication Services. It queues the message and doesn't wait for delivery.</summary>
public sealed class AcsEmailSender(EmailClient client, string senderAddress) : IEmailSender
{
    public static AcsEmailSender Create(EmailOptions options, TokenCredential credential) =>
        new(new EmailClient(new Uri(options.Endpoint), credential), options.SenderAddress);

    public Task SendAsync(DomainEmail message, CancellationToken cancellationToken) =>
        client.SendAsync(
            WaitUntil.Started,
            new Azure.Communication.Email.EmailMessage(senderAddress, message.To, new EmailContent(message.Subject) { PlainText = message.Body }),
            cancellationToken);
}

/// <summary>The <c>Email</c> configuration section: the Communication Services endpoint and the verified sender.</summary>
public sealed record EmailOptions
{
    public const string Section = "Email";

    public string Endpoint { get; init; } = "";

    public string SenderAddress { get; init; } = "";
}

public static class AzureCredentials
{
    /// <summary>The Azure CLI sign-in on a development machine, the app's user-assigned managed identity in Azure.</summary>
    public static TokenCredential Create(bool isDevelopment, string? managedIdentityClientId) => isDevelopment
        ? new Azure.Identity.AzureCliCredential()
        : new Azure.Identity.ManagedIdentityCredential(Azure.Identity.ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId));
}
