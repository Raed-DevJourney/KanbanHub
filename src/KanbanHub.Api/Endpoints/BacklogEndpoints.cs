using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Domain;
using KanbanHub.Domain.Cards;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>The backlog: add cards, order the Ready list, sort cards into priority buckets.</summary>
public sealed class BacklogEndpoints(Backlog backlog)
{
    [Function("CreateCard")]
    public async Task<IActionResult> CreateCard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/boards/{boardId:guid}/cards")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<CardContent>(cancellationToken) is not { } content)
            return Problems.BodyRequired();

        Outcome<CardCreated> outcome = await backlog.AddAsync(boardId, content, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(card =>
        {
            request.HttpContext.Response.Headers.ETag = ETags.Format(card.RowVersion);
            return OutcomeResults.Created(new CardCreatedDto(card.CardId, card.CardNumber));
        });
    }

    [Function("ReorderBacklog")]
    public async Task<IActionResult> Reorder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "v1/boards/{boardId:guid}/backlog/order")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<BacklogOrder>(cancellationToken) is not { Ready: not null, Buckets: not null } order)
            return Problems.BodyRequired();

        Outcome<Saved> outcome = await backlog.ReorderAsync(boardId, order, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }
}
