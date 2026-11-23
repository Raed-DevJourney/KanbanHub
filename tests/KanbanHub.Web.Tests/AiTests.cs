using System.Net;
using KanbanHub.Contracts;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using KanbanHub.Web.Api;
using KanbanHub.Web.State;
using KanbanHub.Web.Tests.Infrastructure;
using static KanbanHub.Web.Tests.Infrastructure.SampleBoard;

namespace KanbanHub.Web.Tests;

/// <summary>How the web app shows what the AI suggests, and when it offers AI at all.</summary>
public class AiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, "Member", true)]
    [InlineData(false, "Member", false)]     // the board did not opt in
    [InlineData(true, "Viewer", false)]      // viewers may read insights, not ask the AI
    public async Task The_ai_buttons_appear_only_on_a_board_that_opted_in_for_people_who_may_use_ai(bool optedIn, string roleName, bool expected)
    {
        var server = new StubServer();
        BoardRole role = BuiltInRoles.All.Single(candidate => candidate.Name == roleName);
        server.OnGet($"boards/{Id}/me", new BoardAccessResponse(role.Permissions.Names()));
        server.OnGet($"boards/{Id}", With() with { AiEnabled = optedIn });
        var store = new BoardStore(server.CreateApi(), new Notices());

        await store.LoadAsync(Id, Ct);

        Assert.Equal(expected, store.CanUseAi);
    }

    [Fact]
    public async Task A_suggestion_comes_back_typed_and_a_refusal_is_explained_to_the_person()
    {
        var server = new StubServer();
        var notices = new Notices();
        Guid card = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        server.On(HttpMethod.Post, $"cards/{card}/ai/breakdown", HttpStatusCode.OK,
            """{ "tasks": [ { "title": "Collect the email address", "acceptanceCriteria": ["Email is validated"], "estimatedDays": 1.5 } ] }""");
        server.OnProblem(HttpMethod.Post, $"cards/{other}/ai/breakdown", 422, "validation-failed", "AI is switched off for this board.");
        KanbanApi api = server.CreateApi();

        BreakdownProposal? proposal = await api.ProposeBreakdownAsync(card, Ct).OrExplainAsync(notices);
        BreakdownProposal? refused = await api.ProposeBreakdownAsync(other, Ct).OrExplainAsync(notices);

        ProposedTask task = Assert.Single(Assert.IsType<BreakdownProposal>(proposal).Tasks);
        Assert.Equal("Collect the email address (about 1.5 days): Email is validated", AiSuggestions.Describe(task));
        Assert.Null(refused);
        Assert.Equal("AI is switched off for this board.", Assert.Single(notices.Toasts).Message);
    }

    [Fact]
    public async Task Triage_enums_and_insights_travel_as_the_api_sends_them()
    {
        var server = new StubServer();
        Guid card = Guid.NewGuid();
        Guid step = Guid.NewGuid();
        server.On(HttpMethod.Post, $"cards/{card}/ai/triage", HttpStatusCode.OK,
            """{ "cardType": "Bug", "priorityBucket": 1, "origin": "B", "why": "It describes a failure." }""");
        server.On(HttpMethod.Get, $"boards/{Id}/insights", HttpStatusCode.OK, $$"""
            [ { "id": 7, "kind": "WipAdvice", "title": "Raise Validate from 3 to 4", "summary": "It swings.", "cards": [12],
                "proposal": { "stepId": "{{step}}", "stepName": "Validate", "currentLimit": 3, "proposedLimit": 4 },
                "createdAtUtc": "2026-11-16T09:00:00Z" } ]
            """);
        KanbanApi api = server.CreateApi();

        object? triage = (await api.SuggestTriageAsync(card, Ct)).Value;
        object? insights = (await api.ListInsightsAsync(Id, Ct)).Value;

        Assert.Equal(new TriageSuggestion(Domain.Cards.CardType.Bug, 1, "B", "It describes a failure."), triage);
        InsightDto insight = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<InsightDto>>(insights));
        Assert.Equal((7L, AiFeature.WipAdvice, new WipProposal(step, "Validate", 3, 4)), (insight.Id, insight.Kind, insight.Proposal));
        Assert.Equal([12], insight.Cards);
    }

    [Fact]
    public async Task Accepting_and_dismissing_an_insight_post_to_its_own_address()
    {
        var server = new StubServer();
        server.On(HttpMethod.Post, $"boards/{Id}/insights/7/accept", HttpStatusCode.NoContent);
        server.On(HttpMethod.Post, $"boards/{Id}/insights/7/dismiss", HttpStatusCode.NoContent);
        KanbanApi api = server.CreateApi();

        object? accepted = (await api.AcceptInsightAsync(Id, 7, Ct)).Value;
        object? dismissed = (await api.DismissInsightAsync(Id, 7, Ct)).Value;

        Assert.IsType<Done>(accepted);
        Assert.IsType<Done>(dismissed);
    }

    [Fact]
    public async Task A_cards_possible_duplicates_arrive_with_the_card_and_are_dismissed_as_a_pair()
    {
        var server = new StubServer();
        Guid card = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        server.On(HttpMethod.Get, $"cards/{card}", HttpStatusCode.OK, $$"""
            { "card": { "id": "{{card}}", "number": 14, "title": "Checkout crashes on an empty cart", "eTag": "AAAAAAAAAAE=" },
              "boardId": "{{Id}}", "createdBy": "{{Guid.NewGuid()}}",
              "duplicates": [ { "cardId": "{{other}}", "number": 9, "title": "Checkout crashes on empty cart", "similarityPercent": 100 } ] }
            """);
        server.On(HttpMethod.Post, $"cards/{card}/ai/duplicates/{other}/dismiss", HttpStatusCode.NoContent);
        KanbanApi api = server.CreateApi();

        object? detail = (await api.GetCardAsync(card, Ct)).Value;
        object? dismissed = (await api.DismissDuplicateAsync(card, other, Ct)).Value;

        Assert.Equal(new DuplicateDto(other, 9, "Checkout crashes on empty cart", 100), Assert.Single(Assert.IsType<CardDetailDto>(detail).Duplicates));
        Assert.IsType<Done>(dismissed);
    }

    [Fact]
    public void An_insight_cites_cards_by_the_boards_own_titles()
    {
        CardDto known = Card("Guest checkout", Implement, Location.Active) with { Number = 12 };

        Assert.Equal("#12 Guest checkout", AiSuggestions.CiteCard(12, [known]));
        Assert.Equal("#99", AiSuggestions.CiteCard(99, [known]));   // archived or gone: the number still says which card it was
        Assert.Equal("WIP advice", AiSuggestions.KindOf(AiFeature.WipAdvice));
        Assert.Equal("Constraint", AiSuggestions.KindOf(AiFeature.Bottleneck));
        Assert.Equal("Standup brief", AiSuggestions.KindOf(AiFeature.StandupBrief));
    }

    [Fact]
    public async Task The_ai_status_reads_the_boards_budget_and_usage()
    {
        var server = new StubServer();
        server.OnGet($"boards/{Id}/ai/status", new AiStatusDto(IsConfigured: true, IsEnabled: true, MonthlyTokenBudget: 1000, TokensUsedThisMonth: 1200));

        object? status = (await server.CreateApi().GetAiStatusAsync(Id, Ct)).Value;

        Assert.True(Assert.IsType<AiStatusDto>(status).IsOverBudget);
    }
}
