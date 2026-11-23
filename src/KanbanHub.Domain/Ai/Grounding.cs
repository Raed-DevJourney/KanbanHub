using System.Globalization;
using System.Text.RegularExpressions;

namespace KanbanHub.Domain.Ai;

/// <summary>
/// The checks every AI answer passes before anyone sees it (section 11.2): it may cite only cards the data holds,
/// and every number it states must be one the data supplied.
/// </summary>
public static partial class Grounding
{
    /// <summary>Every card the answer lists is one of the known cards.</summary>
    public static bool CitesOnly(IEnumerable<int> cited, IEnumerable<int> known) => !cited.Except(known).Any();

    /// <summary>Every card written into free text ("#12") is one of the known cards.</summary>
    public static bool MentionsOnly(string? text, IEnumerable<int> known) => CitesOnly(CardsIn(text), known);

    public static IReadOnlyList<int> CardsIn(string? text) =>
    [
        .. CardReference().Matches(text ?? "").Select(match => int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture))
    ];

    /// <summary>
    /// Every number stated in the text is one the data supplied, allowing for rounding: "2.7" is fine where the
    /// data says 2.667. Card references and numbers glued to letters (P0, 85th) are not statements of quantity.
    /// </summary>
    public static bool NumbersComeFrom(string? text, IEnumerable<decimal> supplied)
    {
        decimal[] known = [.. supplied];
        return StatedNumber().Matches(text ?? "").All(match => IsKnown(match.Value, known));
    }

    /// <summary>True when the name is one of the known names (a step of the board, an item of a done rule), whatever its case.</summary>
    public static bool IsOneOf(string? name, IEnumerable<string> known) =>
        name is not null && known.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    private static bool IsKnown(string stated, decimal[] known)
    {
        decimal value = decimal.Parse(stated, CultureInfo.InvariantCulture);
        int decimals = stated.Contains('.', StringComparison.Ordinal) ? stated.Length - stated.IndexOf('.', StringComparison.Ordinal) - 1 : 0;
        return known.Any(number => Math.Round(number, decimals, MidpointRounding.AwayFromZero) == value);
    }

    [GeneratedRegex(@"#(\d{1,9})\b")]
    private static partial Regex CardReference();

    [GeneratedRegex(@"(?<![\w#.])\d+(?:\.\d+)?(?![\w])")]
    private static partial Regex StatedNumber();
}
