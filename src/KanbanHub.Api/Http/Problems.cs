using KanbanHub.Domain;
using KanbanHub.Domain.Flow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KanbanHub.Api.Http;

/// <summary>Every error the API returns, as RFC 9457 problem details (specification section 5.5).</summary>
internal static class Problems
{
    public const string ContentType = "application/problem+json";

    private const string TypeBase = "https://kanbanhub.example/problems/";

    public static ObjectResult Unauthorized() =>
        Create(401, "unauthorized", "Sign-in required", "A valid Entra access token is required.");

    public static ObjectResult BodyRequired() =>
        Create(400, "body-required", "Body required", "A JSON body of the documented shape is required.");

    public static ObjectResult IdempotencyKeyRequired() =>
        Create(400, "idempotency-key-required", "Idempotency key required", "An Idempotency-Key header is required.");

    public static ObjectResult IfMatchRequired() =>
        Create(428, "if-match-required", "ETag required", "An If-Match header with the card ETag is required.");

    public static ObjectResult InvalidSignature() =>
        Create(401, "invalid-signature", "Signature rejected", "The delivery's X-Hub-Signature-256 doesn't match.");

    public static ObjectResult GitHubUnavailable() =>
        Create(502, "github-unavailable", "GitHub didn't answer", "GitHub refused or didn't answer. Try again in a moment.");

    public static ObjectResult Unexpected() =>
        Create(500, "unexpected", "Unexpected error", "Something went wrong on our side. Try again.");

    public static ObjectResult Forbidden(Forbidden forbidden) =>
        Create(403, "forbidden", "Forbidden", $"Missing permission: {forbidden.Missing}.");

    public static ObjectResult Forbidden(TenantRoleRequired required) =>
        Create(403, "forbidden", "Forbidden", $"Missing tenant role: {required.Role}.");

    public static ObjectResult NotFound(NotFound missing) =>
        Create(404, "not-found", "Not found", $"There is no such {missing.What}.");

    public static ObjectResult Invalid(Invalid invalid) =>
        Create(422, "validation-failed", "Validation failed", string.Join(" ", invalid.Problems))
            .With("errors", invalid.Problems);

    public static ObjectResult IllegalTransition(IllegalTransition illegal) =>
        Create(422, "illegal-transition", "Move not allowed", illegal.Reason);

    public static ObjectResult DoneRuleUnmet(DoneRuleUnmet unmet) =>
        Create(422, "done-rule-unmet", "Done rule not met", "The step's done rule isn't met yet.")
            .With("unmetItems", unmet.UnmetItems);

    public static ObjectResult PreconditionFailed(ConcurrencyConflict conflict) =>
        Create(412, "precondition-failed", "The card changed", "The card changed. Reload and try again.")
            .With("currentETag", ETags.Format(conflict.CurrentRowVersion));

    public static ObjectResult WipLimitReached(WipLimitReached full) =>
        Create(409, "wip-limit-reached", "WIP limit reached", Describe(full.Violations))
            .With("violations", full.Violations)
            .With("swarmCandidates", full.SwarmCandidates);

    public static ObjectResult OverrideReasonRequired(OverrideReasonRequired required) =>
        Create(428, "override-reason-required", "Reason required", "This limit can be exceeded only with a reason.")
            .With("violations", required.Violations);

    /// <summary>Writes the problem straight to the response, for middleware that runs outside MVC.</summary>
    public static async Task WriteToAsync(this ObjectResult problem, HttpResponse response, CancellationToken cancellationToken)
    {
        response.StatusCode = problem.StatusCode ?? StatusCodes.Status500InternalServerError;
        await response.WriteAsJsonAsync((ProblemDetails)problem.Value!, options: null, ContentType, cancellationToken);
    }

    private static ObjectResult Create(int status, string type, string title, string detail)
    {
        var problem = new ProblemDetails { Type = TypeBase + type, Title = title, Status = status, Detail = detail };
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { ContentType } };
    }

    private static ObjectResult With(this ObjectResult result, string name, object value)
    {
        ((ProblemDetails)result.Value!).Extensions[name] = value;
        return result;
    }

    /// <summary>"Stop starting, start finishing": say which step is full and what to do instead.</summary>
    private static string Describe(IReadOnlyList<WipViolation> violations) =>
        violations is [var first, ..]
            ? $"{first.ScopeName} is at {first.Current} of {first.Limit}. Help finish a card in {first.ScopeName} before starting another."
            : "A WIP limit is reached.";
}
