using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Domain.Ai;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>
/// AI help with one card (section 11.1), for people with <c>ai.use</c>. Each answer is a suggestion and nothing
/// more: the caller applies it, or doesn't, through the ordinary card endpoints.
/// </summary>
public sealed class CardAiEndpoints(CardAssistant assistant, DuplicateDesk duplicates)
{
    /// <summary>Says that two cards are not the same work: the suggestion goes from both, and isn't made again.</summary>
    [Function("DismissDuplicate")]
    public async Task<IActionResult> DismissDuplicate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/ai/duplicates/{otherCardId:guid}/dismiss")] HttpRequest request,
        Guid cardId, Guid otherCardId, CancellationToken cancellationToken) =>
        (await duplicates.DismissAsync(cardId, otherCardId, request.HttpContext.User, cancellationToken)).ToResult(OutcomeResults.NoContent);

    [Function("ProposeBreakdown")]
    public async Task<IActionResult> Breakdown(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/ai/breakdown")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken) =>
        (await assistant.ProposeBreakdownAsync(cardId, request.HttpContext.User, cancellationToken)).ToResult(Ok);

    [Function("SuggestTriage")]
    public async Task<IActionResult> Triage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/ai/triage")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken) =>
        (await assistant.SuggestTriageAsync(cardId, request.HttpContext.User, cancellationToken)).ToResult(Ok);

    [Function("ReviewDoneRule")]
    public async Task<IActionResult> DoneRuleCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/ai/done-rule-check")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken) =>
        (await assistant.ReviewDoneRuleAsync(cardId, request.HttpContext.User, cancellationToken)).ToResult(Ok);

    [Function("DraftAgentInstructions")]
    public async Task<IActionResult> AgentInstructions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/ai/agent-instructions")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken) =>
        (await assistant.DraftAgentInstructionsAsync(cardId, request.HttpContext.User, cancellationToken)).ToResult(Ok);

    private static IActionResult Ok(object suggestion) => new OkObjectResult(suggestion);
}

/// <summary>What the AI has to say about a board: its status, its insights, and the forecast in words.</summary>
public sealed class BoardAiEndpoints(AiStatusDesk status, InsightDesk insights, ForecastNarrator narrator)
{
    [Function("GetAiStatus")]
    public async Task<IActionResult> Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/boards/{boardId:guid}/ai/status")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken) =>
        (await status.GetAsync(boardId, request.HttpContext.User, cancellationToken)).ToResult(found => new OkObjectResult(ToDto(found)));

    [Function("NarrateForecast")]
    public async Task<IActionResult> ForecastNarrative(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/boards/{boardId:guid}/ai/forecast-narrative")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken) =>
        (await narrator.NarrateAsync(boardId, request.HttpContext.User, cancellationToken)).ToResult(told => new OkObjectResult(told));

    [Function("ListInsights")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/boards/{boardId:guid}/insights")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken) =>
        (await insights.ListAsync(boardId, request.HttpContext.User, cancellationToken))
        .ToResult(open => new OkObjectResult(open.Select(ToDto).ToList()));

    [Function("DismissInsight")]
    public async Task<IActionResult> Dismiss(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/boards/{boardId:guid}/insights/{insightId:long}/dismiss")] HttpRequest request,
        Guid boardId, long insightId, CancellationToken cancellationToken) =>
        (await insights.DismissAsync(boardId, insightId, request.HttpContext.User, cancellationToken)).ToResult(OutcomeResults.NoContent);

    /// <summary>Accepts a proposed WIP change: the one place a suggestion becomes a change, made by the person who accepts.</summary>
    [Function("AcceptInsight")]
    public async Task<IActionResult> Accept(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/boards/{boardId:guid}/insights/{insightId:long}/accept")] HttpRequest request,
        Guid boardId, long insightId, CancellationToken cancellationToken) =>
        (await insights.AcceptAsync(boardId, insightId, request.HttpContext.User, cancellationToken)).ToResult(OutcomeResults.NoContent);

    private static AiStatusDto ToDto(AiStatus found) =>
        new(found.IsConfigured, found.Policy.IsEnabled, found.Policy.MonthlyTokenBudget, found.Policy.TokensUsedThisMonth);

    private static InsightDto ToDto(StoredInsight stored) => new()
    {
        Id = stored.Id,
        Kind = stored.Content.Kind,
        Title = stored.Content.Title,
        Summary = stored.Content.Summary,
        Points = stored.Content.Points,
        Cards = stored.Content.Cards,
        Proposal = stored.Content.Proposal,
        CreatedAtUtc = stored.CreatedAt.UtcDateTime
    };
}
