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

/// <summary>The conversation on a card.</summary>
public sealed class CommentEndpoints(Comments comments, CommentStore store, ICardStore cards, IBoardAuthorizer authorizer)
{
    [Function("ListComments")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/cards/{cardId:guid}/comments")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        if (await cards.FindFactsAsync(cardId, cancellationToken) is not { } card)
            return Problems.NotFound(new NotFound("card"));
        if (await authorizer.DenyUnlessAsync(request.HttpContext.User, card.BoardId, Permission.BoardRead, cancellationToken) is { } denied)
            return Problems.Forbidden(denied);

        return new OkObjectResult(await store.ListAsync(cardId, cancellationToken));
    }

    [Function("AddComment")]
    public async Task<IActionResult> Add(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/comments")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<AddCommentRequest>(cancellationToken) is not { } body) return Problems.BodyRequired();

        Outcome<CommentAdded> outcome = await comments.AddAsync(cardId, body.Body, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(added => OutcomeResults.Created(new CommentAddedDto(added.CommentId)));
    }
}
