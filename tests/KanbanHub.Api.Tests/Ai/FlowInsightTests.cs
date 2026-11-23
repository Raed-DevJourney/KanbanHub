using System.Security.Claims;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests.Ai;

/// <summary>The background analysis of a board (features 1 to 3 of section 11.1) and what people do with its insights.</summary>
public class FlowInsightTests(KanbanDatabase database)
{
    [Fact]
    public async Task The_standup_brief_lists_what_finished_what_is_blocked_and_what_is_aging_with_the_boards_own_titles()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        Guid finished = await ai.AddCardAtAsync("Guest checkout", ai.Board.Validate, Location.Done);
        Guid blocked = await ai.AddCardAtAsync("Tax service", ai.Board.Implement, Location.Active);
        Guid aging = await ai.AddCardAtAsync("Saved cards", ai.Board.Implement, Location.Active);
        await ai.Board.Cards.Block(Requests.From(ai.Board.Owner, new Blocker("Waiting for the vendor", BlockerCategory.External)), blocked, Ct);
        await ai.AgeAsync(blocked, days: 4);
        await ai.AgeAsync(aging, days: 6);

        await ai.RunAsync(AiFeature.StandupBrief);

        InsightDto brief = Assert.Single(await ai.InsightsAsync());
        Assert.Equal((AiFeature.StandupBrief, "Standup brief"), (brief.Kind, brief.Title));
        Assert.Equal("1 finished since yesterday, 1 blocked, 1 aging.", brief.Summary);
        Assert.Contains($"Finished: #{await ai.NumberOfAsync(finished)} Guest checkout", brief.Points);
        Assert.Contains($"Blocked for 4 days: #{await ai.NumberOfAsync(blocked)} Tax service (Waiting for the vendor)", brief.Points);
        Assert.Contains($"At risk: #{await ai.NumberOfAsync(aging)} Saved cards, 6 days in Implement", brief.Points);
        Assert.Equal(3, brief.Cards.Count);
    }

    /// <summary>The grounding post-check of section 11.2: an insight that cites a card or a number not in the data never appears.</summary>
    [Theory]
    [InlineData(999, "1 finished since yesterday.")]
    [InlineData(null, "42 cards finished since yesterday.")]
    [InlineData(null, "Card #999 is late.")]
    public async Task A_brief_that_invents_a_card_or_a_number_is_not_stored(int? inventedCard, string summary)
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        await ai.AddCardAtAsync("Guest checkout", ai.Board.Validate, Location.Done);
        ai.Model.Answer(request =>
        {
            int[] finished = [.. ((FlowSnapshot)request.Data).Finished.Select(card => card.Number), .. inventedCard is { } card ? [card] : Array.Empty<int>()];
            return new StandupBrief(finished, [], [], [], summary);
        });

        await ai.RunAsync(AiFeature.StandupBrief);

        Assert.Empty(await ai.InsightsAsync());
    }

    [Fact]
    public async Task A_new_brief_replaces_the_one_before()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        await ai.AddCardAtAsync("Guest checkout", ai.Board.Validate, Location.Done);

        await ai.RunAsync(AiFeature.StandupBrief);
        long first = Assert.Single(await ai.InsightsAsync()).Id;
        await ai.RunAsync(AiFeature.StandupBrief);

        Assert.NotEqual(first, Assert.Single(await ai.InsightsAsync()).Id);
    }

    /// <summary>The book's troubleshooting rule: cards piling up in a step's Done mean the next step is the constraint.</summary>
    [Fact]
    public async Task Cards_waiting_in_done_name_the_next_step_as_the_constraint_and_the_insight_goes_when_they_are_pulled()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        Guid first = await ai.AddCardAtAsync("Promo codes", ai.Board.Specify, Location.Done);
        Guid second = await ai.AddCardAtAsync("Gift receipts", ai.Board.Specify, Location.Done);
        await ai.AgeAsync(first, days: 3);

        await ai.RunAsync(AiFeature.Bottleneck);
        InsightDto found = Assert.Single(await ai.InsightsAsync());
        await ai.Board.MustMoveAsync(first, ai.Board.Implement, Location.Active);
        await ai.Board.MustMoveAsync(second, ai.Board.Implement, Location.Active);
        int askedBefore = ai.Model.Requests.Count;
        await ai.RunAsync(AiFeature.Bottleneck);

        Assert.Equal((AiFeature.Bottleneck, "Implement is the constraint"), (found.Kind, found.Title));
        Assert.Equal("Implement is the constraint: Specify Done holds 2 cards, the oldest for 3 days.", found.Summary);
        Assert.Equal(2, found.Cards.Count);
        Assert.Empty(await ai.InsightsAsync());                    // nothing waits any more: the insight is withdrawn
        Assert.Equal(askedBefore, ai.Model.Requests.Count);        // and the rule found that out without asking the model
    }

    /// <summary>The acceptance scenario of section 19.2, "AI suggests, never acts".</summary>
    [Fact]
    public async Task The_wip_advisor_proposes_a_change_and_the_limit_stays_until_a_flow_manager_accepts_it()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        ClaimsPrincipal member = await ai.Board.UserWithAsync(BuiltInRoles.Member);
        ClaimsPrincipal flowManager = await ai.Board.UserWithAsync(BuiltInRoles.FlowManager);
        await ai.Board.SetLimitsAsync(ai.Board.Owner,
            new StepLimit(ai.Board.Implement.Id, 5, WipPolicy.Strict), new StepLimit(ai.Board.Validate.Id, 3, WipPolicy.AllowWithReason));
        ai.Model.Answer(_ => new WipAdvice([new WipSuggestionLine("Validate", 3, 4, "Validate swings between starved and full.")]));

        await ai.RunAsync(AiFeature.WipAdvice);
        InsightDto advice = Assert.Single(await ai.InsightsAsync());
        int? limitAfterAdvice = await LimitOfAsync(ai, ai.Board.Validate);
        IActionResult asMember = await ai.Board.BoardAi.Accept(Requests.From(member), ai.Board.BoardId, advice.Id, Ct);
        int? limitAfterMember = await LimitOfAsync(ai, ai.Board.Validate);
        IActionResult asFlowManager = await ai.Board.BoardAi.Accept(Requests.From(flowManager), ai.Board.BoardId, advice.Id, Ct);

        BoardSnapshotDto board = await ai.Board.SnapshotAsync();
        StepDto validate = board.Steps.Single(step => step.Id == ai.Board.Validate.Id);
        Assert.Equal("Raise Validate from 3 to 4", advice.Title);
        Assert.Equal(new WipProposal(ai.Board.Validate.Id, "Validate", 3, 4), advice.Proposal);
        Assert.Equal((3, 3), (limitAfterAdvice, limitAfterMember));
        asMember.ProblemOf(403, "forbidden");
        Assert.Equal(204, asFlowManager.StatusOf());
        Assert.Equal((4, WipPolicy.AllowWithReason), (validate.WipLimit, validate.Policy));
        Assert.Equal(5, board.Steps.Single(step => step.Id == ai.Board.Implement.Id).WipLimit);   // the other limits are untouched
        Assert.Empty(await ai.InsightsAsync());
    }

    [Fact]
    public async Task Accepting_is_audited_as_the_decision_of_the_person_who_accepted()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        await ai.Board.SetLimitsAsync(ai.Board.Owner, new StepLimit(ai.Board.Validate.Id, 3, WipPolicy.Strict));
        ai.Model.Answer(_ => new WipAdvice([new WipSuggestionLine("Validate", 3, 2, "Validate is faster than the steps around it.")]));
        await ai.RunAsync(AiFeature.WipAdvice);
        long advice = Assert.Single(await ai.InsightsAsync()).Id;

        await ai.Board.BoardAi.Accept(Requests.From(ai.Board.Owner), ai.Board.BoardId, advice, Ct);

        var audit = (await ai.Board.Insights.Audit(Requests.From(ai.Board.Owner), ai.Board.BoardId, Ct)).ValueOf<IReadOnlyList<AuditEntryDto>>();
        AuditEntryDto change = audit.First(entry => entry.EventType == "wip.limit");
        AuditEntryDto accepted = Assert.Single(audit, entry => entry.EventType == "insight.accepted");
        Assert.Equal((ai.Board.Owner.GetObjectId(), "Accepted the AI advisor's suggestion for Validate: 3 to 2."), (change.ActorId, change.Reason));
        Assert.Equal((ai.Board.Owner.GetObjectId(), "Lower Validate from 3 to 2"), (accepted.ActorId, accepted.NewValue));
    }

    [Theory]
    [InlineData("Validate", 3, 5)]   // more than one at a time
    [InlineData("Validate", 2, 3)]   // not the limit the step has
    [InlineData("Validate", 3, 3)]   // no change at all
    [InlineData("Deploy", 3, 4)]     // no such step
    [InlineData("Specify", 3, 4)]    // a step without a limit
    public async Task Advice_that_does_not_fit_the_board_or_the_books_one_at_a_time_rule_is_not_stored(string step, int current, int proposed)
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        await ai.Board.SetLimitsAsync(ai.Board.Owner, new StepLimit(ai.Board.Validate.Id, 3, WipPolicy.Strict));
        ai.Model.Answer(_ => new WipAdvice([new WipSuggestionLine(step, current, proposed, "Because.")]));

        await ai.RunAsync(AiFeature.WipAdvice);

        Assert.Empty(await ai.InsightsAsync());
    }

    [Fact]
    public async Task A_suggestion_goes_stale_when_someone_changes_the_limit_first()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        await ai.Board.SetLimitsAsync(ai.Board.Owner, new StepLimit(ai.Board.Validate.Id, 3, WipPolicy.Strict));
        ai.Model.Answer(_ => new WipAdvice([new WipSuggestionLine("Validate", 3, 4, "Validate swings between starved and full.")]));
        await ai.RunAsync(AiFeature.WipAdvice);
        long advice = Assert.Single(await ai.InsightsAsync()).Id;
        await ai.Board.SetLimitsAsync(ai.Board.Owner, new StepLimit(ai.Board.Validate.Id, 6, WipPolicy.Strict));

        IActionResult accepted = await ai.Board.BoardAi.Accept(Requests.From(ai.Board.Owner), ai.Board.BoardId, advice, Ct);

        accepted.ProblemOf(422, "validation-failed");
        Assert.Equal(6, await LimitOfAsync(ai, ai.Board.Validate));
    }

    [Fact]
    public async Task Everyone_who_sees_the_board_can_read_insights_and_flow_managers_dismiss_them()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        ClaimsPrincipal viewer = await ai.Board.UserWithAsync(BuiltInRoles.Viewer);
        ClaimsPrincipal stranger = await ai.Board.UserWithAsync(role: null);
        await ai.AddCardAtAsync("Guest checkout", ai.Board.Validate, Location.Done);
        await ai.RunAsync(AiFeature.StandupBrief);
        long brief = Assert.Single(await ai.InsightsAsync(viewer)).Id;

        IActionResult asStranger = await ai.Board.BoardAi.List(Requests.From(stranger), ai.Board.BoardId, Ct);
        IActionResult asViewer = await ai.Board.BoardAi.Dismiss(Requests.From(viewer), ai.Board.BoardId, brief, Ct);
        IActionResult nothingToAccept = await ai.Board.BoardAi.Accept(Requests.From(ai.Board.Owner), ai.Board.BoardId, brief, Ct);
        IActionResult asOwner = await ai.Board.BoardAi.Dismiss(Requests.From(ai.Board.Owner), ai.Board.BoardId, brief, Ct);
        IActionResult again = await ai.Board.BoardAi.Dismiss(Requests.From(ai.Board.Owner), ai.Board.BoardId, brief, Ct);

        asStranger.ProblemOf(403, "forbidden");
        asViewer.ProblemOf(403, "forbidden");
        nothingToAccept.ProblemOf(422, "validation-failed");
        Assert.Equal(204, asOwner.StatusOf());
        again.ProblemOf(404, "not-found");
        Assert.Empty(await ai.InsightsAsync());
    }

    /// <summary>Background jobs are queued only for boards that opted in (section 11.2).</summary>
    [Fact]
    public async Task The_scheduler_queues_analysis_only_for_boards_that_opted_in()
    {
        AiScenario optedIn = await AiScenario.StartAsync(database);
        BoardScenario notOptedIn = await BoardScenario.StartAsync(database);
        await optedIn.AddCardAtAsync("Promo codes", optedIn.Board.Specify, Location.Done);
        await optedIn.AddCardAtAsync("Gift receipts", optedIn.Board.Specify, Location.Done);
        Guid waiting = await notOptedIn.AddCardAtAsync(notOptedIn.Specify, Location.Done);
        await notOptedIn.AddCardAtAsync(notOptedIn.Specify, Location.Done);

        await optedIn.Board.Service<AiScheduler>().QueueForEveryBoardAsync(AiFeature.Bottleneck, Ct);

        var enabled = await optedIn.Board.Service<IAiPolicyStore>().ListEnabledBoardsAsync(Ct);
        Assert.Contains(enabled, board => board.BoardId == optedIn.Board.BoardId);
        Assert.DoesNotContain(enabled, board => board.BoardId == notOptedIn.BoardId);
        Assert.Single(await optedIn.InsightsAsync());
        Assert.NotEqual(Guid.Empty, waiting);
    }

    private static async Task<int?> LimitOfAsync(AiScenario ai, StepDto step) =>
        (await ai.Board.SnapshotAsync()).Steps.Single(found => found.Id == step.Id).WipLimit;
}
