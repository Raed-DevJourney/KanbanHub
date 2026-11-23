using System.Security.Claims;
using KanbanHub.Api.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace KanbanHub.Api.Auth;

/// <summary>Lets a request reach a function only with a valid Entra access token, and tells the function who is calling.</summary>
public sealed class EntraJwtMiddleware(EntraTokenValidator validator) : IFunctionsWorkerMiddleware
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>Functions that authenticate differently: the health probe, and (later) the HMAC-signed GitHub webhook.</summary>
    private static readonly HashSet<string> Anonymous = ["Health", "GitHubWebhook"];

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        HttpContext? http = context.GetHttpContext();
        if (http is null || Anonymous.Contains(context.FunctionDefinition.Name))
        {
            await next(context);
            return;
        }

        if (await AuthenticateAsync(http.Request, context.CancellationToken) is not { } user)
        {
            await Problems.Unauthorized().WriteToAsync(http.Response, context.CancellationToken);
            return;
        }

        http.User = user;
        await next(context);
    }

    private async Task<ClaimsPrincipal?> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        string header = request.Headers.Authorization.ToString();
        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? await validator.ValidateAsync(header[BearerPrefix.Length..], cancellationToken)
            : null;
    }
}
