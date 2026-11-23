using KanbanHub.Contracts;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Web.State;

public static class SubColumns
{
    /// <summary>
    /// What a location is called on screen. The book's Track sub-column is shown as "Blocked":
    /// it is where a blocked card goes, outside the WIP count.
    /// </summary>
    public static string NameOf(Location location) => location switch
    {
        Location.Track => "Blocked",
        Location.Parked => "Parking lot",
        _ => location.ToString()
    };
}

/// <summary>A place on the board a card can sit: a step's sub-column, the backlog or the parking lot.</summary>
public sealed record BoardSlot(StepDto? Step, Location Location)
{
    public static BoardSlot Backlog { get; } = new(null, Location.Backlog);

    public static BoardSlot ParkingLot { get; } = new(null, Location.Parked);

    public Guid? StepId => Step?.Id;

    public string Label => Step is null ? SubColumns.NameOf(Location) : $"{Step.Name} {SubColumns.NameOf(Location)}";

    public bool Holds(CardDto card) => card.Location == Location && card.StepId == StepId;

    public CardPosition ToPosition() => Step is null
        ? CardPosition.OutsideSteps(Location)
        : CardPosition.InStep(Step.Id, Step.Order, Location);

    /// <summary>Every slot of a board in reading order: backlog, each step's sub-columns, parking lot.</summary>
    public static IReadOnlyList<BoardSlot> AllOf(IEnumerable<StepDto> steps) =>
    [
        Backlog,
        .. steps.SelectMany(SlotsOf),
        ParkingLot
    ];

    private static IEnumerable<BoardSlot> SlotsOf(StepDto step)
    {
        yield return new BoardSlot(step, Location.Active);
        if (step.HasTrack) yield return new BoardSlot(step, Location.Track);
        yield return new BoardSlot(step, Location.Done);
    }
}
