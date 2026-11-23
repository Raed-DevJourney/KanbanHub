using KanbanHub.Domain.Events;
using KanbanHub.Domain.Notifications;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KanbanHub.DevHost;

/// <summary>Writes the email to the log instead of sending it.</summary>
internal sealed partial class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        LogEmail(logger, message.To, message.Subject);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email to {To}: {Subject}")]
    private static partial void LogEmail(ILogger logger, string to, string subject);
}

/// <summary>
/// On this machine every consumer runs in the request's own process, so one that fails is logged and skipped:
/// it must not hold up the others or the outbox. (In Azure each has its own subscription, retries and dead-letter queue.)
/// </summary>
internal sealed partial class Forgiving(IBoardEventConsumer inner, ILogger<Forgiving> logger) : IBoardEventConsumer
{
    public async Task HandleAsync(BoardEvent boardEvent, CancellationToken cancellationToken)
    {
        try
        {
            await inner.HandleAsync(boardEvent, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSkipped(logger, inner.GetType().Name, boardEvent.Type, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Consumer} failed on {EventType}; skipped.")]
    private static partial void LogSkipped(ILogger logger, string consumer, string eventType, Exception exception);
}

internal static class LocalEvents
{
    /// <summary>Registers a consumer of board events behind <see cref="Forgiving"/>.</summary>
    public static IServiceCollection AddConsumer<T>(this IServiceCollection services)
        where T : class, IBoardEventConsumer
    {
        services.TryAddSingleton<T>();
        return services.AddSingleton<IBoardEventConsumer>(provider =>
            new Forgiving(provider.GetRequiredService<T>(), provider.GetRequiredService<ILogger<Forgiving>>()));
    }

    /// <summary>No Service Bus, SignalR Service or Communication Services here: one process does all three jobs.</summary>
    public static IServiceCollection AddLocalEvents(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddHttpContextAccessor();
        services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, DevUserIdProvider>();
        services.AddSingleton<IRealtimeGateway, LocalHubGateway>();
        services.AddSingleton<IEmailSender, LogEmailSender>();
        services.AddConsumer<BoardBroadcaster>();
        services.AddConsumer<NotificationGenerator>();
        services.AddSingleton<IBoardEventPublisher, InProcessPublisher>();
        return services;
    }

    /// <summary>
    /// Publishes what a request wrote to the outbox once the request is done, as the API host does.
    /// There is no sweeper here, so it doesn't stop when the caller hangs up.
    /// </summary>
    public static void UseOutboxPublishing(this WebApplication app) => app.Use(async (http, next) =>
    {
        await next(http);

        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method))
            await http.RequestServices.GetRequiredService<OutboxDispatcher>()
                .DispatchAsync(minimumAge: TimeSpan.Zero, app.Lifetime.ApplicationStopping);
    });
}
