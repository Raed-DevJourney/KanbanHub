namespace KanbanHub.Domain.Flow;

/// <summary>A cap on the cards a scope may hold, and what happens when a move would exceed it (rules R6 and R8).</summary>
public sealed record class WipLimit
{
    /// <summary>The book's floor: a step always has room for one card.</summary>
    public const int Minimum = 1;

    public const int Maximum = 99;

    public WipLimit(int maxItems, WipPolicy policy = WipPolicy.Strict)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, Minimum);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxItems, Maximum);
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown WIP policy.");

        MaxItems = maxItems;
        Policy = policy;
    }

    public int MaxItems { get; }

    public WipPolicy Policy { get; }

    public bool Allows(int count) => count <= MaxItems;

    /// <summary>Brings a computed suggestion into the legal range.</summary>
    public static int Clamp(int maxItems) => Math.Clamp(maxItems, Minimum, Maximum);
}
