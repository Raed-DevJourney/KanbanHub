using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Domain;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Planning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>A board's policies: WIP limits (with the calculator), done rules and members.</summary>
public sealed class BoardSettingsEndpoints(BoardSettings settings)
{
    [Function("SetWipLimits")]
    public async Task<IActionResult> SetWipLimits(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "v1/boards/{boardId:guid}/wip-limits")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<SetWipLimitsRequest>(cancellationToken) is not { Limits: not null } body)
            return Problems.BodyRequired();

        var change = new WipLimitChange(boardId, body.Limits, body.Reason);
        Outcome<Saved> outcome = await settings.SetWipLimitsAsync(change, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    /// <summary>A preview of suggested limits; nothing is saved until the Flow Manager sets them.</summary>
    [Function("SuggestWipLimits")]
    public async Task<IActionResult> SuggestWipLimits(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/boards/{boardId:guid}/wip-calculator")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<WipCalculatorRequest>(cancellationToken) is not { Steps: not null } body)
            return Problems.BodyRequired();

        Outcome<IReadOnlyList<WipSuggestion>> outcome =
            await settings.SuggestWipLimitsAsync(boardId, body.Steps, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(suggestions => new OkObjectResult(suggestions));
    }

    [Function("SetDoneRule")]
    public async Task<IActionResult> SetDoneRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "v1/boards/{boardId:guid}/steps/{stepId:guid}/done-rule")] HttpRequest request,
        Guid boardId, Guid stepId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<SetDoneRuleRequest>(cancellationToken) is not { Items: not null } body)
            return Problems.BodyRequired();

        var change = new DoneRuleChange(boardId, stepId, body.CardType, body.Items);
        Outcome<Saved> outcome = await settings.SetDoneRuleAsync(change, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    [Function("SetMembers")]
    public async Task<IActionResult> SetMembers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "v1/boards/{boardId:guid}/members")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<SetMembersRequest>(cancellationToken) is not { Members: not null } body)
            return Problems.BodyRequired();

        Outcome<Saved> outcome = await settings.SetMembersAsync(boardId, body.Members, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }
}
