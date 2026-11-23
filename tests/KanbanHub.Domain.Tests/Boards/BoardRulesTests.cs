using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Planning;

namespace KanbanHub.Domain.Tests.Boards;

public class BoardRulesTests
{
    private static readonly StepDraft Specify = new(Guid.NewGuid(), "Specify", "#7C3AED");
    private static readonly StepDraft Implement = new(Guid.NewGuid(), "Implement", "#2563EB");

    [Fact]
    public void Two_named_colored_steps_make_a_workflow() =>
        Assert.Empty(WorkflowRules.FindProblems([Specify, Implement]));

    [Fact]
    public void A_workflow_has_two_to_twelve_steps()
    {
        StepDraft[] thirteen = [.. Enumerable.Range(1, 13).Select(n => new StepDraft(null, $"Step {n}", "#2563EB"))];

        Assert.Single(WorkflowRules.FindProblems([Specify]));
        Assert.Single(WorkflowRules.FindProblems(thirteen));
    }

    [Fact]
    public void Steps_need_unique_names_valid_colors_and_may_appear_once()
    {
        Assert.Single(WorkflowRules.FindProblems([Specify, Implement with { Name = " SPECIFY " }]));
        Assert.Single(WorkflowRules.FindProblems([Specify, Implement with { Color = "blue" }]));
        Assert.Single(WorkflowRules.FindProblems([Specify, Implement with { Id = Specify.Id }]));
        Assert.Single(WorkflowRules.FindProblems([Specify, Implement with { Description = new string('d', 1001) }]));
        Assert.Equal(2, WorkflowRules.FindProblems([Specify, Implement with { Name = "" }, new StepDraft(null, "", "#2563EB")]).Count);
    }

    [Theory]
    [InlineData("#2563EB", true)]
    [InlineData("#2563eb", true)]
    [InlineData("2563EB", false)]
    [InlineData("#2563E", false)]
    [InlineData("#2563EG", false)]
    [InlineData(null, false)]
    public void A_color_is_a_hash_and_six_hex_digits(string? color, bool expected) =>
        Assert.Equal(expected, HexColor.IsValid(color));

    [Fact]
    public void The_palette_has_a_color_for_every_status_and_card_type_of_section_6()
    {
        Assert.Equal(10, Palette.Defaults.Keys.Count(key => key.StartsWith("status.", StringComparison.Ordinal)));
        Assert.Equal(9, Palette.Defaults.Keys.Count(key => key.StartsWith("type.", StringComparison.Ordinal)));
        Assert.All(Palette.Defaults.Values, color => Assert.True(HexColor.IsValid(color)));
    }

    [Fact]
    public void Overrides_replace_known_tokens_only()
    {
        IReadOnlyDictionary<string, string> palette = Palette.Effective(
            [new PaletteColor("status.blocked", "#b91c1c"), new PaletteColor("status.imaginary", "#000000")]);

        Assert.Equal("#B91C1C", palette["status.blocked"]);
        Assert.Equal(Palette.Defaults.Count, palette.Count);
    }

    [Fact]
    public void A_palette_change_names_every_problem()
    {
        IReadOnlyList<string> problems = Palette.FindProblems(
        [
            new PaletteColor("status.imaginary", "#000000"),
            new PaletteColor("status.blocked", "red"),
            new PaletteColor("status.blocked", "#B91C1C")
        ]);

        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void Sensible_preferences_have_no_problems() =>
        Assert.Empty(new BoardPreferences { Name = "Flow", ReadyListCap = 10, TimeZoneId = "UTC" }.FindProblems());

    [Fact]
    public void Each_preference_out_of_range_is_reported()
    {
        var preferences = new BoardPreferences
        {
            Name = " ",
            ReadyListCap = 0,
            ParkingReviewWeeks = 0,
            ArchiveAfterDays = 400,
            TimeZoneId = "Mars/Olympus_Mons"
        };

        Assert.Equal(5, preferences.FindProblems().Count);
    }

    [Fact]
    public void A_forecast_divides_the_work_left_by_the_net_completion_rate()
    {
        var inputs = new ForecastInputs(new TaskTotals(5, 0, 0), new TaskTotals(3, 1, 1), FinishedInWindow: 1, Days: 14);

        Forecast forecast = Forecast.From(inputs);

        Assert.Equal(56, forecast.DaysRemaining!.Value, precision: 6);
        Assert.False(forecast.NeverFinishes);
    }

    [Fact]
    public void A_forecast_warns_when_work_arrives_as_fast_as_it_finishes()
    {
        var inputs = new ForecastInputs(new TaskTotals(0, 0, 0), new TaskTotals(5, 0, 1), FinishedInWindow: 1, Days: 7);

        Forecast forecast = Forecast.From(inputs);

        Assert.Null(forecast.DaysRemaining);
        Assert.True(forecast.NeverFinishes);
    }

    [Fact]
    public void A_board_with_nothing_left_is_finished_not_stuck()
    {
        var inputs = new ForecastInputs(new TaskTotals(0, 0, 0), new TaskTotals(0, 0, 2), FinishedInWindow: 2, Days: 7);

        Forecast forecast = Forecast.From(inputs);

        Assert.Equal(0, forecast.DaysRemaining);
        Assert.False(forecast.NeverFinishes);
    }
}
