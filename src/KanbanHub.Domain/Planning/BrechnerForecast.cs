namespace KanbanHub.Domain.Planning;

/// <summary>Task counts at one moment: estimated but not started, in progress, and finished.</summary>
public readonly record struct TaskTotals(int PendingEstimated, int Active, int Done)
{
    public int All => PendingEstimated + Active + Done;
}

/// <summary>The book's three flow numbers: task completion rate, task add rate, current task estimate.</summary>
public readonly record struct FlowRates(double CompletionPerDay, double AddPerDay, int CurrentTaskEstimate);

/// <summary>The forecast from Brechner chapter 3 (specification section 15.4, FR-P02).</summary>
public static class BrechnerForecast
{
    /// <summary>
    /// TCR = tasks finished in the window / days; TAR = growth of all tasks / days; CTE = pending + active.
    /// </summary>
    public static FlowRates Rates(TaskTotals start, TaskTotals end, int finishedInWindow, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);

        return new FlowRates(
            CompletionPerDay: (double)finishedInWindow / days,
            AddPerDay: (double)(end.All - start.All) / days,
            CurrentTaskEstimate: end.PendingEstimated + end.Active);
    }

    /// <summary>Days remaining = CTE / (TCR - TAR). Null means the work never finishes at the current rates.</summary>
    public static double? DaysRemaining(FlowRates rates) =>
        rates.CompletionPerDay > rates.AddPerDay
            ? rates.CurrentTaskEstimate / (rates.CompletionPerDay - rates.AddPerDay)
            : null;
}
