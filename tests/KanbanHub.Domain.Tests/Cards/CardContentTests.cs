using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;

namespace KanbanHub.Domain.Tests.Cards;

public class CardContentTests
{
    private static readonly CardContent Valid = new() { Title = "Retry policy for export job" };

    [Fact]
    public void A_title_is_all_a_card_needs() => Assert.Empty(Valid.FindProblems());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_card_without_a_title_is_refused(string title) =>
        Assert.Single((Valid with { Title = title }).FindProblems());

    [Fact]
    public void A_title_longer_than_300_characters_is_refused() =>
        Assert.Single((Valid with { Title = new string('x', 301) }).FindProblems());

    [Theory]
    [InlineData("U")]
    [InlineData("X")]
    [InlineData("F")]
    [InlineData("S")]
    [InlineData("L")]
    [InlineData("TF")]
    [InlineData("PF")]
    [InlineData("B")]
    public void The_books_origin_codes_are_accepted(string origin) =>
        Assert.Empty((Valid with { Origin = origin }).FindProblems());

    [Fact]
    public void Any_other_origin_is_refused() =>
        Assert.Single((Valid with { Origin = "Q" }).FindProblems());

    [Fact]
    public void Buckets_run_from_P0_to_P3()
    {
        Assert.Empty((Valid with { PriorityBucket = 0 }).FindProblems());
        Assert.Empty((Valid with { PriorityBucket = 3 }).FindProblems());
        Assert.Single((Valid with { PriorityBucket = 4 }).FindProblems());
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var content = new CardContent
        {
            Title = "",
            CardType = (CardType)99,
            PriorityBucket = 9,
            Origin = "??",
            TargetRelease = new string('r', 101),
            EstimatedTasks = -1
        };

        Assert.Equal(6, content.FindProblems().Count);
    }

    [Fact]
    public void The_quick_start_template_is_the_books_board()
    {
        BoardTemplate template = BoardTemplates.QuickStart;

        Assert.Equal(["Specify", "Implement", "Validate"], template.Steps.Select(step => step.Name));
        Assert.Equal([2, 5, 3], template.Steps.Select(step => step.WipLimit));
        Assert.True(template.Steps[0].CountsParentsOnly);
        Assert.Same(template, BoardTemplates.Find("QUICK-START"));
        Assert.Null(BoardTemplates.Find("nope"));
    }
}
