using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.Planning;

/// <summary>What the team can do in one step: how many people work it and how fast each one is.</summary>
public sealed record class StepCapacity
{
    public StepCapacity(string stepName, decimal itemsPerPerson, int people)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemsPerPerson);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(people);

        StepName = stepName;
        ItemsPerPerson = itemsPerPerson;
        People = people;
    }

    public string StepName { get; }

    /// <summary>Items one person finishes in this step per period (any period, as long as all steps use the same).</summary>
    public decimal ItemsPerPerson { get; }

    public int People { get; }

    public decimal Throughput => ItemsPerPerson * People;
}

/// <summary>A starting limit for one step. A suggestion only: the Flow Manager decides.</summary>
public sealed record class WipSuggestion(string StepName, decimal PeopleNeeded, int SuggestedLimit, bool IsConstraint);

/// <summary>
/// The book's starting WIP limits (Brechner chapter 2, step 3): the slowest step sets the pace,
/// every step is staffed to match its throughput, and each limit is that headcount plus a 50% buffer.
/// </summary>
public static class WipCalculator
{
    public const decimal Buffer = 1.5m;

    public static IReadOnlyList<WipSuggestion> Suggest(IReadOnlyCollection<StepCapacity> steps)
    {
        if (steps.Count == 0) throw new ArgumentException("At least one step is needed.", nameof(steps));

        decimal pace = steps.Min(step => step.Throughput);
        return [.. steps.Select(step => SuggestFor(step, pace))];
    }

    private static WipSuggestion SuggestFor(StepCapacity step, decimal pace)
    {
        decimal peopleNeeded = pace / step.ItemsPerPerson;
        int limit = WipLimit.Clamp((int)Math.Ceiling(peopleNeeded * Buffer));
        return new WipSuggestion(step.StepName, peopleNeeded, limit, IsConstraint: step.Throughput == pace);
    }
}
