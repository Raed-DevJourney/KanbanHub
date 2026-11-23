namespace KanbanHub.Domain.Cards;

/// <summary>The built-in card types (FR-C02). Stored in <c>kb.Card.CardType</c>.</summary>
public enum CardType : byte
{
    Feature = 1,
    Task = 2,
    Bug = 3,
    TechDebt = 4,
    Design = 5,
    Escalation = 6,
    Event = 7,
    Spike = 8,
    AgentTask = 9
}

/// <summary>Why a card is blocked, so blockers can be clustered later (FR-P03).</summary>
public enum BlockerCategory : byte
{
    Dependency = 1,
    Decision = 2,
    Information = 3,
    Environment = 4,
    External = 5,
    Other = 6
}

/// <summary>The fields of a card that people edit directly. Position and flags change through their own commands.</summary>
public sealed record class CardContent
{
    public const int MaxTitleLength = 300;
    public const int MaxTargetReleaseLength = 100;
    public const byte LowestPriorityBucket = 3;

    /// <summary>The book's origin codes: usage, experiment, feedback, support; leadership, team feature, partner feature, bug.</summary>
    public static readonly IReadOnlySet<string> OriginCodes = new HashSet<string> { "U", "X", "F", "S", "L", "TF", "PF", "B" };

    public string Title { get; init; } = "";

    public string? Description { get; init; }

    public CardType CardType { get; init; } = CardType.Feature;

    /// <summary>P0 (the MVP) to P3, or null while unsorted.</summary>
    public byte? PriorityBucket { get; init; }

    public string? Origin { get; init; }

    public DateOnly? DueDate { get; init; }

    public string? TargetRelease { get; init; }

    public short? EstimatedTasks { get; init; }

    public IReadOnlyList<string> FindProblems()
    {
        List<string> problems = [];
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > MaxTitleLength)
            problems.Add($"A title of 1 to {MaxTitleLength} characters is required.");
        if (!Enum.IsDefined(CardType))
            problems.Add("Unknown card type.");
        if (PriorityBucket > LowestPriorityBucket)
            problems.Add($"The priority bucket must be 0 to {LowestPriorityBucket}.");
        if (Origin is not null && !OriginCodes.Contains(Origin))
            problems.Add($"The origin must be one of {string.Join(", ", OriginCodes)}.");
        if (TargetRelease?.Length > MaxTargetReleaseLength)
            problems.Add($"The target release can be at most {MaxTargetReleaseLength} characters.");
        if (EstimatedTasks < 0)
            problems.Add("The estimated task count can't be negative.");
        return problems;
    }
}
