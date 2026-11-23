using KanbanHub.Domain.Planning;

namespace KanbanHub.Domain.Tests.Planning;

public class BrechnerForecastTests
{
    private static readonly TaskTotals Start = new(PendingEstimated: 10, Active: 3, Done: 2);
    private static readonly TaskTotals End = new(PendingEstimated: 8, Active: 3, Done: 9);

    [Fact]
    public void Rates_come_from_what_finished_and_what_was_added_in_the_window()
    {
        FlowRates rates = BrechnerForecast.Rates(Start, End, finishedInWindow: 7, days: 5);

        Assert.Equal(1.4, rates.CompletionPerDay, precision: 10);
        Assert.Equal(1.0, rates.AddPerDay, precision: 10);
        Assert.Equal(11, rates.CurrentTaskEstimate);
    }

    [Fact]
    public void Days_remaining_is_the_estimate_over_the_net_completion_rate()
    {
        var rates = new FlowRates(CompletionPerDay: 1.4, AddPerDay: 1.0, CurrentTaskEstimate: 11);

        double? days = BrechnerForecast.DaysRemaining(rates);

        Assert.Equal(27.5, days!.Value, precision: 10);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.0, 1.5)]
    public void Work_added_as_fast_as_it_finishes_never_ends(double completionPerDay, double addPerDay)
    {
        var rates = new FlowRates(completionPerDay, addPerDay, CurrentTaskEstimate: 20);

        Assert.Null(BrechnerForecast.DaysRemaining(rates));
    }

    [Fact]
    public void A_shrinking_backlog_shortens_the_forecast()
    {
        var rates = new FlowRates(CompletionPerDay: 2, AddPerDay: -1, CurrentTaskEstimate: 12);

        Assert.Equal(4, BrechnerForecast.DaysRemaining(rates));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_window_needs_at_least_one_day(int days) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BrechnerForecast.Rates(Start, End, finishedInWindow: 7, days));
}
