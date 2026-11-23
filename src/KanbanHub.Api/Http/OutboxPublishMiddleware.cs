using KanbanHub.Domain.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace KanbanHub.Api.Http;

/// <summary>
/// Publishes the events a request just wrote to the outbox, right after its transaction committed
/// (specification section 9.3). If publishing fails the request still succeeded: the workers' sweeper
/// picks the events up within a minute.
/// </summary>
public sealed partial class OutboxPublishMiddleware(OutboxDispatcher dispatcher, ILogger<OutboxPublishMiddleware> logger) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        await next(context);

        if (context.GetHttpContext() is { } http && !HttpMethods.IsGet(http.Request.Method))
            await PublishAsync(context.CancellationToken);
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dispatcher.DispatchAsync(minimumAge: TimeSpan.Zero, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLeftForSweeper(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing outbox events failed; the sweeper will retry.")]
    private static partial void LogLeftForSweeper(ILogger logger, Exception exception);
}
