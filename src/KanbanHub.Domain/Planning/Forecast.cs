namespace KanbanHub.Domain.Planning;

/// <summary>What the forecast is computed from: task counts at both ends of a window, and what finished inside it.</summary>
public sealed record class ForecastInputs(TaskTotals Start, TaskTotals End, int FinishedInWindow, int Days);

/// <summary>The book's forecast (FR-P02): the three rates and when the remaining work finishes.</summary>
public sealed record class Forecast(double CompletionPerDay, double AddPerDay, int CurrentTaskEstimate, double? DaysRemaining)
{
    /// <summary>The red warning: work is added at least as fast as it finishes, and there is work left.</summary>
    public bool NeverFinishes => DaysRemaining is null && CurrentTaskEstimate > 0;

    public static Forecast From(ForecastInputs inputs)
    {
        FlowRates rates = BrechnerForecast.Rates(inputs.Start, inputs.End, inputs.FinishedInWindow, inputs.Days);
        double? days = rates.CurrentTaskEstimate == 0 ? 0 : BrechnerForecast.DaysRemaining(rates);
        return new Forecast(rates.CompletionPerDay, rates.AddPerDay, rates.CurrentTaskEstimate, days);
    }
}
