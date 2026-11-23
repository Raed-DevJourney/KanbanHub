using KanbanHub.Domain;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KanbanHub.Api.Http;

/// <summary>
/// Turns domain outcomes into HTTP. Both switches are over unions, so a new outcome
/// fails the build here until it has a status code.
/// </summary>
internal static class OutcomeResults
{
    public static IActionResult ToResult<T>(this Outcome<T> outcome, Func<T, IActionResult> onSuccess)
        where T : notnull => outcome switch
        {
            T value => onSuccess(value),
            Forbidden forbidden => Problems.Forbidden(forbidden),
            TenantRoleRequired required => Problems.Forbidden(required),
            NotFound missing => Problems.NotFound(missing),
            Invalid invalid => Problems.Invalid(invalid),
            ConcurrencyConflict conflict => Problems.PreconditionFailed(conflict),
        };

    /// <summary>The status codes of specification section 5.5.</summary>
    public static IActionResult ToResult(this MoveOutcome outcome, HttpResponse response) => outcome switch
    {
        Moved moved => response.OkWithETag(moved.RowVersion, moved),
        WipLimitReached full => Problems.WipLimitReached(full),
        OverrideReasonRequired required => Problems.OverrideReasonRequired(required),
        DoneRuleUnmet unmet => Problems.DoneRuleUnmet(unmet),
        ConcurrencyConflict conflict => Problems.PreconditionFailed(conflict),
        IllegalTransition illegal => Problems.IllegalTransition(illegal),
        Forbidden forbidden => Problems.Forbidden(forbidden),
    };

    public static IActionResult NoContent(Saved _) => new NoContentResult();

    public static IActionResult Created(object body) => new ObjectResult(body) { StatusCode = StatusCodes.Status201Created };

    /// <summary>A card changed: no body, but the new ETag for the caller's next write.</summary>
    public static IActionResult ToETagResult(this Outcome<CardVersion> outcome, HttpResponse response) =>
        outcome.ToResult(version => response.OkWithETag(version.RowVersion));
}
