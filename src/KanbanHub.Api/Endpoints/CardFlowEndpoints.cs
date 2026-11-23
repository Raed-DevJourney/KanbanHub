using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Domain;
using KanbanHub.Domain.Flow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>
/// The two ways a card changes place: a move and a cancellation. Both need the card's ETag in
/// <c>If-Match</c> and an <c>Idempotency-Key</c>, and both end in the enforcing procedure.
/// </summary>
public sealed class CardFlowEndpoints(MoveCardHandler mover, CancelCardHandler canceler)
{
    [Function("MoveCard")]
    public async Task<IActionResult> Move(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/move")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        if (!request.TryGetIdempotencyKey(out Guid key)) return Problems.IdempotencyKeyRequired();
        if (!request.TryGetIfMatch(out byte[] expected)) return Problems.IfMatchRequired();
        if (await request.ReadBodyAsync<MoveCardBody>(cancellationToken) is not { } body) return Problems.BodyRequired();

        var command = new MoveCardCommand
        {
            CardId = cardId,
            ExpectedRowVersion = expected,
            ToStepId = body.ToStepId,
            ToLocation = body.ToLocation,
            ToRank = body.ToRank,
            Reason = body.OverrideReason,
            IdempotencyKey = key
        };

        return await OrNotFoundAsync(async () =>
            (await mover.HandleAsync(command, request.HttpContext.User, cancellationToken)).ToResult(request.HttpContext.Response));
    }

    [Function("CancelCard")]
    public async Task<IActionResult> Cancel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/cancel")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        if (!request.TryGetIdempotencyKey(out Guid key)) return Problems.IdempotencyKeyRequired();
        if (!request.TryGetIfMatch(out byte[] expected)) return Problems.IfMatchRequired();
        if (await request.ReadBodyAsync<CancelCardRequest>(cancellationToken) is not { } body) return Problems.BodyRequired();

        var command = new CancelCardCommand
        {
            CardId = cardId,
            ExpectedRowVersion = expected,
            Reason = body.Reason,
            IdempotencyKey = key
        };

        return await OrNotFoundAsync(async () =>
            (await canceler.HandleAsync(command, request.HttpContext.User, cancellationToken)).ToResult(request.HttpContext.Response));
    }

    /// <summary>The move union has no "not found" case (specification section 5.3), so the repository throws for it.</summary>
    private static async Task<IActionResult> OrNotFoundAsync(Func<Task<IActionResult>> move)
    {
        try
        {
            return await move();
        }
        catch (CardNotFoundException)
        {
            return Problems.NotFound(new NotFound("card"));
        }
    }
}
