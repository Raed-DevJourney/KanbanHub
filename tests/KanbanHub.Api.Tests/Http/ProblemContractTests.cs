using System.Text.Json;
using System.Text.Json.Nodes;
using KanbanHub.Api.Http;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KanbanHub.Api.Tests.Http;

/// <summary>The error contract of specification section 5.5, and the small HTTP conventions around it.</summary>
public class ProblemContractTests
{
    private static readonly byte[] RowVersion = [0, 0, 0, 0, 0, 0, 7, 209];
    private static readonly WipViolation Full = new(WipScope.Step, "Implement", Limit: 5, Current: 5, WipPolicy.Strict);

    [Fact]
    public void A_move_returns_the_card_with_its_new_etag()
    {
        var response = new DefaultHttpContext().Response;
        var moved = new Moved(Guid.NewGuid(), Guid.NewGuid(), MovementId: 9, RowVersion, Recorded: []);

        IActionResult result = new MoveOutcome(moved).ToResult(response);

        Assert.Same(moved, result.ValueOf<Moved>());
        Assert.Equal("\"AAAAAAAAB9E=\"", response.Headers.ETag.ToString());
    }

    [Fact]
    public void A_full_step_is_a_409_that_says_where_to_help()
    {
        Guid[] candidates = [Guid.NewGuid(), Guid.NewGuid()];

        ProblemDetails problem = Map(new WipLimitReached([Full], candidates)).ProblemOf(409, "wip-limit-reached");

        Assert.Equal("WIP limit reached", problem.Title);
        Assert.Equal("Implement is at 5 of 5. Help finish a card in Implement before starting another.", problem.Detail);
        Assert.Equal([Full], Assert.IsAssignableFrom<IReadOnlyList<WipViolation>>(problem.Extensions["violations"]));
        Assert.Equal(candidates, problem.Extensions["swarmCandidates"]);
    }

    [Fact]
    public void A_limit_that_needs_a_reason_is_a_428() =>
        Map(new OverrideReasonRequired([Full])).ProblemOf(428, "override-reason-required");

    [Fact]
    public void An_unmet_done_rule_is_a_422_listing_what_is_missing()
    {
        ProblemDetails problem = Map(new DoneRuleUnmet(["Unit tests pass"])).ProblemOf(422, "done-rule-unmet");

        Assert.Equal(["Unit tests pass"], Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.Extensions["unmetItems"]));
    }

    [Fact]
    public void An_illegal_move_is_a_422_with_the_rule_it_breaks() =>
        Assert.Equal("Not here.", Map(new IllegalTransition("Not here.")).ProblemOf(422, "illegal-transition").Detail);

    [Fact]
    public void A_stale_etag_is_a_412_with_the_current_one()
    {
        ProblemDetails problem = Map(new ConcurrencyConflict(RowVersion)).ProblemOf(412, "precondition-failed");

        Assert.Equal("\"AAAAAAAAB9E=\"", problem.Extensions["currentETag"]);
    }

    [Fact]
    public void A_missing_permission_is_a_403_that_names_it() =>
        Assert.Equal("Missing permission: CardMove.", Map(new Forbidden(Permission.CardMove)).ProblemOf(403, "forbidden").Detail);

    [Fact]
    public void Use_case_failures_map_to_403_404_and_422()
    {
        Outcome<Saved> roleRequired = new TenantRoleRequired(TenantRoles.BoardCreator);
        Outcome<Saved> missing = new NotFound("board");
        Outcome<Saved> invalid = new Invalid(["A title is required.", "Unknown card type."]);

        Assert.Contains("KanbanHub.BoardCreator", roleRequired.ToResult(OutcomeResults.NoContent).ProblemOf(403, "forbidden").Detail);
        Assert.Equal("There is no such board.", missing.ToResult(OutcomeResults.NoContent).ProblemOf(404, "not-found").Detail);
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<string>>(
            invalid.ToResult(OutcomeResults.NoContent).ProblemOf(422, "validation-failed").Extensions["errors"]).Count);
    }

    [Fact]
    public void An_etag_is_the_row_version_in_base64_and_quotes()
    {
        Assert.True(ETags.TryParse(ETags.Format(RowVersion), out byte[] parsed));
        Assert.Equal(RowVersion, parsed);
        Assert.False(ETags.TryParse("\"not base64\"", out _));
        Assert.False(ETags.TryParse("\"AAAA\"", out _));
    }

    [Fact]
    public void A_merge_patch_changes_what_it_names_clears_what_it_nulls_and_keeps_the_rest()
    {
        var current = new CardContent { Title = "Old", Description = "Keep or clear", PriorityBucket = 1, Origin = "F" };
        JsonObject patch = JsonNode.Parse("""{ "title": "New", "description": null, "cardType": "Bug" }""")!.AsObject();

        CardContent? merged = patch.MergeInto(current);

        Assert.Equal(new CardContent { Title = "New", CardType = CardType.Bug, PriorityBucket = 1, Origin = "F" }, merged);
    }

    [Fact]
    public void A_merge_patch_of_the_wrong_shape_yields_nothing() =>
        Assert.Null(JsonNode.Parse("""{ "priorityBucket": "high" }""")!.AsObject().MergeInto(new CardContent { Title = "T" }));

    [Fact]
    public void Contracts_travel_as_camel_case_with_enums_by_name()
    {
        string json = JsonSerializer.Serialize(
            new MoveCardBody(ToStepId: null, Location.Parked), KanbanHubJsonContext.Default.MoveCardBody);
        MoveCardBody? fromNumber = JsonSerializer.Deserialize(
            """{ "toLocation": 1, "overrideReason": "Hotfix" }""", KanbanHubJsonContext.Default.MoveCardBody);

        Assert.Equal("""{"toLocation":"Parked"}""", json);
        Assert.Equal(new MoveCardBody(null, Location.Active, OverrideReason: "Hotfix"), fromNumber);
    }

    private static IActionResult Map(MoveOutcome outcome) => outcome.ToResult(new DefaultHttpContext().Response);
}
