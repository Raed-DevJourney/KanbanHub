using System.Text.Json.Nodes;
using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Data.Cards;
using KanbanHub.Domain;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>One card: read it, edit its content, block it, tick its done rule, break it down.</summary>
public sealed class CardEndpoints(CardCommands commands, CardBlocking blocking, CardQueries queries, IBoardAuthorizer authorizer)
{
    [Function("GetCard")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/cards/{cardId:guid}")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        if (await queries.FindAsync(cardId, cancellationToken) is not { } card)
            return Problems.NotFound(new NotFound("card"));
        if (await authorizer.DenyUnlessAsync(request.HttpContext.User, card.BoardId, Permission.BoardRead, cancellationToken) is { } denied)
            return Problems.Forbidden(denied);

        request.HttpContext.Response.Headers.ETag = $"\"{card.Card.ETag}\"";
        return new OkObjectResult(card);
    }

    /// <summary>A JSON merge patch over the card's content: send only the fields that change, <c>null</c> to clear one.</summary>
    [Function("EditCard")]
    public async Task<IActionResult> Edit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "v1/cards/{cardId:guid}")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        if (!request.TryGetIfMatch(out byte[] expected)) return Problems.IfMatchRequired();
        if (await request.ReadBodyAsync<JsonObject>(cancellationToken) is not { } patch) return Problems.BodyRequired();

        var edit = new CardEdit(cardId, expected, patch.MergeInto);
        Outcome<CardVersion> outcome = await commands.EditAsync(edit, request.HttpContext.User, cancellationToken);
        return outcome.ToETagResult(request.HttpContext.Response);
    }

    [Function("BlockCard")]
    public async Task<IActionResult> Block(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/block")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<Blocker>(cancellationToken) is not { } blocker) return Problems.BodyRequired();

        Outcome<BlockChange> outcome = await blocking.BlockAsync(cardId, blocker, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(change => ToResult(change, request.HttpContext.Response));
    }

    [Function("UnblockCard")]
    public async Task<IActionResult> Unblock(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/unblock")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        Outcome<BlockChange> outcome = await blocking.UnblockAsync(cardId, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(change => ToResult(change, request.HttpContext.Response));
    }

    [Function("ToggleChecklistItem")]
    public async Task<IActionResult> ToggleChecklistItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/checklist/{itemId:guid}/toggle")] HttpRequest request,
        Guid cardId, Guid itemId, CancellationToken cancellationToken)
    {
        Outcome<Saved> outcome = await commands.ToggleChecklistItemAsync(cardId, itemId, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    [Function("BreakDownCard")]
    public async Task<IActionResult> BreakDown(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/breakdown")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<BreakDownRequest>(cancellationToken) is not { Titles: not null } body)
            return Problems.BodyRequired();

        Outcome<IReadOnlyList<Guid>> outcome =
            await commands.BreakDownAsync(cardId, body.Titles, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(children => OutcomeResults.Created(new BreakDownResponse(children)));
    }

    private static IActionResult ToResult(BlockChange change, HttpResponse response) =>
        response.OkWithETag(change.RowVersion, new BlockResponse(change.Placement));
}
