using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.GitHub;

/// <summary>
/// The labels KanbanHub maintains on linked issues (section 9.4): where the card is, whether it is blocked,
/// its priority bucket and its type. Every other label belongs to the people using GitHub and is never touched.
/// </summary>
public static class KanbanLabels
{
    public const string Done = "kanban:done";
    public const string Blocked = "kanban:blocked";

    private const string StepPrefix = "kanban:";
    private const string PriorityPrefix = "priority:";
    private const string TypePrefix = "type:";

    private static readonly string[] ReservedPrefixes = [StepPrefix, PriorityPrefix, TypePrefix];

    public static bool IsReserved(string label) =>
        ReservedPrefixes.Any(prefix => label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The maintained labels a card's issue should carry, in a stable order.</summary>
    public static IReadOnlyList<string> For(SyncedCard card) => [.. Wanted(card).Order(StringComparer.Ordinal)];

    /// <summary>The issue's own labels with the maintained ones replaced by <paramref name="maintained"/>.</summary>
    public static IReadOnlyList<string> Merge(IEnumerable<string> onIssue, IEnumerable<string> maintained) =>
        [.. onIssue.Where(label => !IsReserved(label)), .. maintained];

    public static string ForStep(string stepName) => StepPrefix + Slug(stepName);

    public static string ForType(CardType type) => TypePrefix + Hyphenate(type.ToString());

    public static string ForPriority(byte bucket) => $"{PriorityPrefix}P{bucket}";

    /// <summary>The bucket a <c>priority:P0</c> to <c>priority:P3</c> label names.</summary>
    public static byte? PriorityOf(string label)
    {
        for (byte bucket = 0; bucket <= CardContent.LowestPriorityBucket; bucket++)
        {
            if (Same(label, ForPriority(bucket))) return bucket;
        }

        return null;
    }

    public static CardType? TypeOf(string label)
    {
        CardType[] matches = [.. Enum.GetValues<CardType>().Where(type => Same(label, ForType(type)))];
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>The step a <c>kanban:&lt;step&gt;</c> label names. The sub-column labels are not steps.</summary>
    public static SyncedStep? StepOf(string label, IEnumerable<SyncedStep> steps) =>
        Same(label, Done) || Same(label, Blocked) ? null : steps.FirstOrDefault(step => Same(label, ForStep(step.Name)));

    public static bool Same(string label, string other) => string.Equals(label, other, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Wanted(SyncedCard card)
    {
        if (card.Step is { } step && card.Location.IsInsideStep()) yield return ForStep(step.Name);
        if (card.Location is Location.Done) yield return Done;
        if (card.IsBlocked) yield return Blocked;
        if (card.Content.PriorityBucket is { } bucket) yield return ForPriority(bucket);
        yield return ForType(card.Content.CardType);
    }

    /// <summary>"In Review" becomes "in-review".</summary>
    private static string Slug(string name)
    {
        char[] characters = [.. name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')];
        return string.Join('-', new string(characters).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>"TechDebt" becomes "tech-debt".</summary>
    private static string Hyphenate(string pascalCase) =>
        string.Concat(pascalCase.Select((c, index) =>
            index > 0 && char.IsUpper(c) ? $"-{char.ToLowerInvariant(c)}" : $"{char.ToLowerInvariant(c)}"));
}
