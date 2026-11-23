using System.Security.Claims;
using KanbanHub.AI;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests.Ai;

/// <summary>
/// The duplicate finder (section 11.1, feature 8): embeddings stored in a SQL <c>vector</c> column and compared
/// by the database. The stand-in embedder makes two texts as close as the words they share.
/// </summary>
public class DuplicateFinderTests(KanbanDatabase database)
{
    [Fact]
    public async Task A_new_card_that_says_what_another_card_says_is_suggested_as_its_duplicate_on_both_cards()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        Guid first = await AddAsync(ai, "Checkout crashes on empty cart");
        await AddAsync(ai, "Add gift receipts");

        Guid second = await AddAsync(ai, "Checkout crashes on an empty cart");

        DuplicateDto onSecond = Assert.Single((await ai.Board.CardAsync(second)).Duplicates);
        DuplicateDto onFirst = Assert.Single((await ai.Board.CardAsync(first)).Duplicates);
        Assert.Equal((first, "Checkout crashes on empty cart", 100), (onSecond.CardId, onSecond.Title, onSecond.SimilarityPercent));
        Assert.Equal((second, await ai.NumberOfAsync(second)), (onFirst.CardId, onFirst.Number));
    }

    [Fact]
    public async Task Cards_that_share_only_a_word_or_two_are_not_suggested()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        await AddAsync(ai, "Checkout crashes on empty cart");

        Guid other = await AddAsync(ai, "Checkout page needs a new footer design");

        Assert.Empty((await ai.Board.CardAsync(other)).Duplicates);
    }

    /// <summary>Per-board opt-in (section 11.2): on a board that did not opt in, no card text is embedded at all.</summary>
    [Fact]
    public async Task A_board_that_did_not_opt_in_has_nothing_embedded_and_nothing_suggested()
    {
        AiScenario ai = await AiScenario.StartAsync(database, optedIn: false);

        await AddAsync(ai, "Checkout crashes on empty cart");
        Guid second = await AddAsync(ai, "Checkout crashes on empty cart");

        Assert.Empty((await ai.Board.CardAsync(second)).Duplicates);
        Assert.Equal(0, ai.Board.Service<WordHashEmbedder>().Embedded);
    }

    [Fact]
    public async Task Only_cards_of_the_same_board_are_compared()
    {
        AiScenario here = await AiScenario.StartAsync(database);
        AiScenario elsewhere = await AiScenario.StartAsync(database);
        await AddAsync(elsewhere, "Checkout crashes on empty cart");

        Guid card = await AddAsync(here, "Checkout crashes on empty cart");

        Assert.Empty((await here.Board.CardAsync(card)).Duplicates);
    }

    [Fact]
    public async Task Saying_two_cards_are_not_duplicates_removes_the_suggestion_from_both_for_good()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        ClaimsPrincipal viewer = await ai.Board.UserWithAsync(BuiltInRoles.Viewer);
        ClaimsPrincipal member = await ai.Board.UserWithAsync(BuiltInRoles.Member);
        Guid first = await AddAsync(ai, "Checkout crashes on empty cart");
        Guid second = await AddAsync(ai, "Checkout crashes on an empty cart");

        IActionResult asViewer = await ai.Board.CardAi.DismissDuplicate(Requests.From(viewer), first, second, Ct);
        IActionResult asMember = await ai.Board.CardAi.DismissDuplicate(Requests.From(member), first, second, Ct);
        IActionResult again = await ai.Board.CardAi.DismissDuplicate(Requests.From(member), second, first, Ct);
        await EditAsync(ai, second, new { description = "Seen on the cart page." });   // compared again: the pair stays dismissed

        asViewer.ProblemOf(403, "forbidden");
        Assert.Equal(204, asMember.StatusOf());
        again.ProblemOf(404, "not-found");
        Assert.Empty((await ai.Board.CardAsync(first)).Duplicates);
        Assert.Empty((await ai.Board.CardAsync(second)).Duplicates);
    }

    [Fact]
    public async Task A_card_is_embedded_again_only_when_its_text_changed_and_a_suggestion_that_no_longer_holds_is_withdrawn()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        var embedder = ai.Board.Service<WordHashEmbedder>();
        await AddAsync(ai, "Checkout crashes on empty cart");
        Guid second = await AddAsync(ai, "Checkout crashes on an empty cart");

        await EditAsync(ai, second, new { priorityBucket = 1 });          // an edit that leaves the text alone
        int afterOtherEdit = embedder.Embedded;
        await EditAsync(ai, second, new { title = "Add gift receipts to the order email" });

        Assert.Equal(2, afterOtherEdit);
        Assert.Equal(3, embedder.Embedded);
        Assert.Empty((await ai.Board.CardAsync(second)).Duplicates);
        Assert.Equal(3 * embedder.TokensPerText, (await ai.StatusAsync()).TokensUsedThisMonth);   // embedding counts toward the budget
    }

    [Fact]
    public async Task Once_the_budget_is_used_up_nothing_more_is_embedded()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        await ai.SetAsync(new { aiMonthlyTokenBudget = 20 });
        await AddAsync(ai, "Checkout crashes on empty cart");   // uses the 20 tokens there are

        Guid second = await AddAsync(ai, "Checkout crashes on an empty cart");

        Assert.Empty((await ai.Board.CardAsync(second)).Duplicates);
        Assert.Equal(1, ai.Board.Service<WordHashEmbedder>().Embedded);
    }

    /// <summary>Adds a card and lets the consumers of its events run, as the host does after a request.</summary>
    private static async Task<Guid> AddAsync(AiScenario ai, string title)
    {
        Guid card = await ai.AddCardAsync(title);
        await ai.Board.PublishEventsOfAsync(card);
        return card;
    }

    private static async Task EditAsync(AiScenario ai, Guid card, object patch)
    {
        IActionResult edited = await ai.Board.Cards.Edit(Requests.From(ai.Board.Owner, patch, await ai.Board.ETagOfAsync(card)), card, Ct);
        Assert.Equal(204, edited.StatusOf());
        await ai.Board.PublishEventsOfAsync(card);
    }
}
