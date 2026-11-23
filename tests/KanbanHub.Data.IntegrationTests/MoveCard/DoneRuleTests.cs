using KanbanHub.Data.IntegrationTests.Infrastructure;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests.MoveCard;

/// <summary>Feature: done rules gate completion (specification sections 2, 5.1 and 19.2).</summary>
public class DoneRuleTests(KanbanDatabase database) : MoveCardTestBase(database)
{
    private static readonly DoneRuleItem[] ImplementRule = [new("Code reviewed"), new("Unit tests pass"), new("Docs updated", IsRequired: false)];

    [Fact]
    public async Task Unmet_done_rule()
    {
        Guid card = await PullIntoImplementAsync();
        await Board.TickAsync(card, "Code reviewed");

        MoveOutcome outcome = await MoveAsync(card, Board.Implement.Done);

        var unmet = Assert.IsType<DoneRuleUnmet>(outcome.Value);
        Assert.Equal(["Unit tests pass"], unmet.UnmetItems);
        Assert.Equal(Location.Active, (await Records.CardAsync(card)).Location);
    }

    [Fact]
    public async Task A_card_finishes_the_step_once_every_required_item_is_ticked()
    {
        Guid card = await PullIntoImplementAsync();
        await Board.TickAsync(card, "Code reviewed");
        await Board.TickAsync(card, "Unit tests pass");

        await MustMoveAsync(card, Board.Implement.Done);
    }

    [Fact]
    public async Task Entering_a_step_gives_the_card_that_steps_checklist()
    {
        Guid card = await PullIntoImplementAsync();

        List<ChecklistRecord> checklist = await Records.ChecklistAsync(card, Board.Implement);

        Assert.Equal(
            [new("Code reviewed", IsRequired: true, IsChecked: false),
             new("Unit tests pass", IsRequired: true, IsChecked: false),
             new("Docs updated", IsRequired: false, IsChecked: false)],
            checklist);
    }

    [Fact]
    public async Task A_rule_for_the_cards_type_replaces_the_steps_general_rule()
    {
        await Board.SetDoneRuleAsync(Board.Implement, ImplementRule);
        await Board.SetDoneRuleAsync(Board.Implement, [new("Root cause found")], cardType: TestBoard.Bug);
        Guid bug = await Board.AddCardAsync(Board.Specify.Done, cardType: TestBoard.Bug);

        await MustMoveAsync(bug, Board.Implement.Active);

        Assert.Equal(["Root cause found"], (await Records.ChecklistAsync(bug, Board.Implement)).Select(item => item.Text));
    }

    [Fact]
    public async Task Moving_to_Track_and_back_keeps_the_ticks()
    {
        Guid card = await PullIntoImplementAsync();
        await Board.TickAsync(card, "Code reviewed");

        await MustMoveAsync(card, Board.Implement.Track);
        await MustMoveAsync(card, Board.Implement.Active);

        Assert.True((await Records.ChecklistAsync(card, Board.Implement))[0].IsChecked);
    }

    [Fact]
    public async Task Work_that_comes_back_to_a_step_must_meet_its_rule_again()
    {
        Guid card = await PullIntoImplementAsync();
        await Board.TickAsync(card, "Code reviewed");
        await Board.TickAsync(card, "Unit tests pass");
        await MustMoveAsync(card, Board.Implement.Done);
        await MustMoveAsync(card, Board.Validate.Active);

        await MustMoveAsync(card, Board.Implement.Active, new MoveOptions { Reason = "Failed validation" });

        Assert.All(await Records.ChecklistAsync(card, Board.Implement), item => Assert.False(item.IsChecked));
    }

    [Fact]
    public async Task A_step_without_a_done_rule_lets_cards_finish()
    {
        Guid card = await Board.AddCardAsync(Board.Specify.Active);

        await MustMoveAsync(card, Board.Specify.Done);
    }

    private async Task<Guid> PullIntoImplementAsync()
    {
        await Board.SetDoneRuleAsync(Board.Implement, ImplementRule);
        Guid card = await Board.AddCardAsync(Board.Specify.Done);
        await MustMoveAsync(card, Board.Implement.Active);
        return card;
    }
}
