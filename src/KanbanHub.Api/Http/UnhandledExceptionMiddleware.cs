using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace KanbanHub.Api.Http;

/// <summary>The last line of defense: an exception nobody expected becomes a 500 problem that reveals nothing.</summary>
public sealed partial class UnhandledExceptionMiddleware(ILogger<UnhandledExceptionMiddleware> logger) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (context.GetHttpContext() is { Response.HasStarted: false } http)
        {
            LogUnhandled(logger, exception, context.FunctionDefinition.Name);
            await Problems.Unexpected().WriteToAsync(http.Response, CancellationToken.None);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception in function {Function}.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string function);
}
