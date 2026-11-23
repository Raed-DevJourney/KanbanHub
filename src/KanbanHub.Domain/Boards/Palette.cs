namespace KanbanHub.Domain.Boards;

/// <summary>One color a board overrides, by design-token key.</summary>
public sealed record class PaletteColor(string Key, string Color);

/// <summary>
/// The colors of specification section 6 as design tokens. Color is never the only signal:
/// each status and type also has an icon and a label, which the UI owns.
/// </summary>
public static class Palette
{
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>
    {
        ["status.canceled"] = "#9CA3AF",
        ["status.blocked"] = "#DC2626",
        ["status.late"] = "#EA580C",
        ["status.atRisk"] = "#D97706",
        ["status.tracked"] = "#CA8A04",
        ["status.parked"] = "#7C3AED",
        ["status.inProgress"] = "#2563EB",
        ["status.waitingForPull"] = "#0D9488",
        ["status.notStarted"] = "#64748B",
        ["status.done"] = "#16A34A",
        ["type.feature"] = "#4F46E5",
        ["type.task"] = "#0284C7",
        ["type.bug"] = "#E11D48",
        ["type.techDebt"] = "#92400E",
        ["type.design"] = "#C026D3",
        ["type.escalation"] = "#C2410C",
        ["type.event"] = "#6D28D9",
        ["type.spike"] = "#4D7C0F",
        ["type.agentTask"] = "#0891B2"
    };

    /// <summary>The defaults with a board's overrides applied.</summary>
    public static IReadOnlyDictionary<string, string> Effective(IEnumerable<PaletteColor> overrides)
    {
        var colors = new Dictionary<string, string>(Defaults);
        foreach (PaletteColor color in overrides.Where(color => Defaults.ContainsKey(color.Key)))
            colors[color.Key] = color.Color.ToUpperInvariant();
        return colors;
    }

    public static IReadOnlyList<string> FindProblems(IReadOnlyList<PaletteColor> overrides)
    {
        List<string> problems =
        [
            .. overrides.Where(color => !Defaults.ContainsKey(color.Key)).Select(color => $"'{color.Key}' is not a color this board has."),
            .. overrides.Where(color => !HexColor.IsValid(color.Color)).Select(color => $"'{color.Color}' is not a color like #2563EB.")
        ];
        if (overrides.Select(color => color.Key).Distinct().Count() != overrides.Count)
            problems.Add("Each color can be set only once.");
        return problems;
    }
}

public static class HexColor
{
    /// <summary>True for the <c>#RRGGBB</c> form the database stores.</summary>
    public static bool IsValid(string? color) =>
        color is ['#', .. { Length: 6 } digits] && digits.All(char.IsAsciiHexDigit);
}
