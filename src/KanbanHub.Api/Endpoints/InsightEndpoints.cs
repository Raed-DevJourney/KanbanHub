using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Data.Boards;
using KanbanHub.Domain;
using KanbanHub.Domain.Planning;
using KanbanHub.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>What a board's history tells: its audit trail and its forecast.</summary>
public sealed class InsightEndpoints(InsightQueries queries, IBoardAuthorizer authorizer)
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    private const int DefaultWindowDays = 14;
    private const int MaxWindowDays = 365;
    private const byte Mvp = 0;   // the forecast is scoped to P0 unless asked otherwise (FR-P02)

    /// <summary>Paged, newest first: pass the last entry's time as <c>before</c> to get the next page.</summary>
    [Function("GetAudit")]
    public async Task<IActionResult> Audit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/boards/{boardId:guid}/audit")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        // Tenant auditors read every board's trail (specification section 7.1).
        if (!request.HttpContext.User.IsInRole(TenantRoles.Auditor)
            && await authorizer.DenyUnlessAsync(request.HttpContext.User, boardId, Permission.AuditView, cancellationToken) is { } denied)
            return Problems.Forbidden(denied);

        DateTimeOffset? before = DateTimeOffset.TryParse(request.Query["before"], out DateTimeOffset parsed) ? parsed : null;
        int take = Math.Clamp(request.Query.IntOr("take", DefaultPageSize), 1, MaxPageSize);
        return new OkObjectResult(await queries.ListAuditAsync(boardId, before, take, cancellationToken));
    }

    /// <summary>
    /// <c>forecast</c> is the book's TCR/TAR/CTE forecast. <c>?bucket=all</c> or <c>?bucket=2</c> changes the scope,
    /// <c>?days=30</c> the window. The chart kinds of FR-P03 arrive with release 2.
    /// </summary>
    [Function("GetMetrics")]
    public async Task<IActionResult> Metrics(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/boards/{boardId:guid}/metrics/{kind}")] HttpRequest request,
        Guid boardId, string kind, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(request.HttpContext.User, boardId, Permission.MetricsView, cancellationToken) is { } denied)
            return Problems.Forbidden(denied);
        if (!string.Equals(kind, "forecast", StringComparison.OrdinalIgnoreCase))
            return Problems.NotFound(new NotFound($"metric '{kind}'"));

        byte? bucket = request.Query["bucket"] == "all" ? null : (byte)request.Query.IntOr("bucket", Mvp);
        int days = Math.Clamp(request.Query.IntOr("days", DefaultWindowDays), 1, MaxWindowDays);
        ForecastInputs inputs = await queries.GetForecastInputsAsync(boardId, bucket, days, cancellationToken);
        return new OkObjectResult(ToDto(Forecast.From(inputs), bucket, days));
    }

    private static ForecastDto ToDto(Forecast forecast, byte? bucket, int days) => new()
    {
        WindowDays = days,
        PriorityBucket = bucket,
        CompletionPerDay = forecast.CompletionPerDay,
        AddPerDay = forecast.AddPerDay,
        CurrentTaskEstimate = forecast.CurrentTaskEstimate,
        DaysRemaining = forecast.DaysRemaining,
        NeverFinishes = forecast.NeverFinishes
    };
}
