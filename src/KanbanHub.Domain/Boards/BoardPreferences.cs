namespace KanbanHub.Domain.Boards;

/// <summary>The board settings an owner edits directly (FR-B04, FR-B07, FR-B08).</summary>
public sealed record class BoardPreferences
{
    public const int MaxReadyListCap = 500;
    public const int MaxParkingReviewWeeks = 52;
    public const int MaxArchiveAfterDays = 365;
    public const int MaxAgentConcurrencyLimit = 20;
    public const int MaxAiMonthlyTokenBudget = 1_000_000_000;

    public string Name { get; init; } = "";

    /// <summary>The most cards the ordered Ready list may hold; null for no cap.</summary>
    public short? ReadyListCap { get; init; }

    /// <summary>How often the parking lot is reviewed.</summary>
    public byte ParkingReviewWeeks { get; init; } = 3;

    /// <summary>Finished cards leave the board this many days after they were done.</summary>
    public short ArchiveAfterDays { get; init; } = 14;

    public TimeOnly? StandupTimeLocal { get; init; }

    public string TimeZoneId { get; init; } = "UTC";

    /// <summary>
    /// How many agent sessions may be open at once (section 10.2): every one of them ends in a pull request
    /// people must review. 0 keeps agents off the board.
    /// </summary>
    public byte AgentConcurrencyLimit { get; init; } = 3;

    /// <summary>The board's opt-in to AI (section 11.2). Off until an owner switches it on.</summary>
    public bool AiEnabled { get; init; }

    /// <summary>Tokens the board may spend on AI in a month. Reaching it stops new requests until the next month.</summary>
    public int AiMonthlyTokenBudget { get; init; } = 2_000_000;

    public IReadOnlyList<string> FindProblems()
    {
        List<string> problems = [];
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > BoardCatalog.MaxNameLength)
            problems.Add($"A name of 1 to {BoardCatalog.MaxNameLength} characters is required.");
        if (ReadyListCap is < 1 or > MaxReadyListCap)
            problems.Add($"The Ready list cap must be 1 to {MaxReadyListCap}, or empty for no cap.");
        if (ParkingReviewWeeks is < 1 or > MaxParkingReviewWeeks)
            problems.Add($"The parking lot review must be every 1 to {MaxParkingReviewWeeks} weeks.");
        if (ArchiveAfterDays is < 1 or > MaxArchiveAfterDays)
            problems.Add($"Finished cards must be archived after 1 to {MaxArchiveAfterDays} days.");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out _))
            problems.Add($"'{TimeZoneId}' is not a known time zone.");
        if (AgentConcurrencyLimit > MaxAgentConcurrencyLimit)
            problems.Add($"At most {MaxAgentConcurrencyLimit} agent sessions can be open at once.");
        if (AiMonthlyTokenBudget is < 0 or > MaxAiMonthlyTokenBudget)
            problems.Add($"The monthly AI budget must be 0 to {MaxAiMonthlyTokenBudget:N0} tokens.");
        return problems;
    }
}
