using KanbanHub.Domain.Planning;

namespace KanbanHub.Domain.Tests.Planning;

public class WipCalculatorTests
{
    /// <summary>
    /// Brechner chapter 2: an analyst specifies 6 items a month, a developer implements 2 and a tester
    /// validates 3. Three developers set the pace, so the starting limits are 2, 5 and 3.
    /// </summary>
    [Fact]
    public void The_books_worked_example_gives_2_5_3()
    {
        IReadOnlyList<WipSuggestion> suggestions = WipCalculator.Suggest(
        [
            new StepCapacity("Specify", itemsPerPerson: 6, people: 1),
            new StepCapacity("Implement", itemsPerPerson: 2, people: 3),
            new StepCapacity("Validate", itemsPerPerson: 3, people: 2)
        ]);

        Assert.Equal([2, 5, 3], suggestions.Select(s => s.SuggestedLimit));
        Assert.Equal([1m, 3m, 2m], suggestions.Select(s => s.PeopleNeeded));
    }

    [Fact]
    public void An_overstaffed_step_is_limited_to_the_people_the_pace_needs()
    {
        IReadOnlyList<WipSuggestion> suggestions = WipCalculator.Suggest(
        [
            new StepCapacity("Specify", itemsPerPerson: 6, people: 4),
            new StepCapacity("Implement", itemsPerPerson: 2, people: 3)
        ]);

        WipSuggestion specify = suggestions[0];
        Assert.Equal(1m, specify.PeopleNeeded);
        Assert.Equal(2, specify.SuggestedLimit);
        Assert.False(specify.IsConstraint);
        Assert.True(suggestions[1].IsConstraint);
    }

    [Fact]
    public void Suggestions_keep_the_order_and_names_of_the_steps()
    {
        IReadOnlyList<WipSuggestion> suggestions = WipCalculator.Suggest(
            [new StepCapacity("Develop", 1, 2), new StepCapacity("Deploy", 4, 1)]);

        Assert.Equal(["Develop", "Deploy"], suggestions.Select(s => s.StepName));
    }

    [Fact]
    public void A_suggestion_never_leaves_the_legal_range()
    {
        IReadOnlyList<WipSuggestion> suggestions = WipCalculator.Suggest(
            [new StepCapacity("Fast", itemsPerPerson: 1000, people: 1), new StepCapacity("Slow", itemsPerPerson: 0.5m, people: 1000)]);

        Assert.Equal([1, 99], suggestions.Select(s => s.SuggestedLimit));
    }

    [Fact]
    public void A_board_without_steps_has_nothing_to_suggest() =>
        Assert.Throws<ArgumentException>(() => WipCalculator.Suggest([]));

    [Theory]
    [InlineData("", 1, 1)]
    [InlineData("Specify", 0, 1)]
    [InlineData("Specify", 1, 0)]
    public void A_capacity_needs_a_name_a_rate_and_people(string stepName, int itemsPerPerson, int people) =>
        Assert.ThrowsAny<ArgumentException>(() => new StepCapacity(stepName, itemsPerPerson, people));
}
