using KanbanHub.Domain.Events;
using Microsoft.AspNetCore.SignalR;

namespace KanbanHub.DevHost;

/// <summary>The local stand-in for Azure SignalR Service. Browsers only listen, so the hub has no methods.</summary>
internal sealed class BoardHub : Hub
{
    public const string Path = "/hubs/board";
}

/// <summary>
/// Names a connection after the user id it presents as its access token. Like the rest of this host
/// it trusts the caller: there is no real sign-in here.
/// </summary>
internal sealed class DevUserIdProvider : IUserIdProvider
{
    private const string Bearer = "Bearer ";

    public string? GetUserId(HubConnectionContext connection)
    {
        HttpRequest? request = connection.GetHttpContext()?.Request;
        if (request?.Query["access_token"].ToString() is { Length: > 0 } fromQuery) return fromQuery;

        string header = request?.Headers.Authorization.ToString() ?? "";
        return header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase) ? header[Bearer.Length..] : null;
    }
}

/// <summary>Real-time through the hub this host serves itself.</summary>
internal sealed class LocalHubGateway(IHubContext<BoardHub> hub, IHttpContextAccessor http) : IRealtimeGateway
{
    public Task<RealtimeConnection> NegotiateAsync(Guid userId, CancellationToken cancellationToken)
    {
        HttpRequest request = http.HttpContext?.Request ?? throw new InvalidOperationException("Negotiation needs a request.");
        return Task.FromResult(new RealtimeConnection($"{request.Scheme}://{request.Host}{BoardHub.Path}", userId.ToString()));
    }

    public Task JoinBoardAsync(string connectionId, Guid boardId, CancellationToken cancellationToken) =>
        hub.Groups.AddToGroupAsync(connectionId, IRealtimeGateway.GroupOf(boardId), cancellationToken);

    public Task BoardChangedAsync(BoardChanged change, CancellationToken cancellationToken) =>
        hub.Clients.Group(IRealtimeGateway.GroupOf(change.BoardId)).SendAsync(
            IRealtimeGateway.BoardChangedMethod, change.BoardId, change.EventType, change.CardId, cancellationToken);

    public Task InboxChangedAsync(Guid userId, CancellationToken cancellationToken) =>
        hub.Clients.User(userId.ToString()).SendAsync(IRealtimeGateway.InboxChangedMethod, cancellationToken);
}
