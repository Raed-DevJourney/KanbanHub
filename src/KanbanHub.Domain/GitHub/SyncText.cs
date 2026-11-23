using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.GitHub;

/// <summary>
/// Card text is untrusted. Before the app puts any of it into Markdown it writes on GitHub, the text loses its
/// power to format, link, mention people or reference issues.
/// </summary>
public static partial class MarkdownText
{
    private const string Punctuation = "\\`*_{}[]()<>#+-!|~";
    private const char ZeroWidthSpace = '​';

    /// <summary>One line of plain text, safe inside the app's own Markdown.</summary>
    public static string Inline(string text)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (char c in Whitespace().Replace(text.Trim(), " "))
        {
            if (Punctuation.Contains(c, StringComparison.Ordinal)) escaped.Append('\\');
            escaped.Append(c);
            if (c is '@' or '#') escaped.Append(ZeroWidthSpace);   // "@name" and "#12" stop being a mention and a reference
        }

        return escaped.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// The one comment the app keeps on a linked issue and edits in place (section 8.3): where the card is,
/// the WIP of its step, why it is blocked, and what could not be applied.
/// </summary>
public static class StatusComment
{
    /// <summary>Marks the comment as the app's, for a person reading the raw text.</summary>
    public const string Marker = "<!-- kanbanhub:status -->";

    public static string Render(SyncedCard card, IssueLink link, string? boardUrl)
    {
        var text = new StringBuilder(Marker).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"**KanbanHub** card #{card.Number}\n\n");
        text.Append(CultureInfo.InvariantCulture, $"- **Where:** {Place(card)}\n");
        if (card.Location.IsInsideStep() && card.Step is { } step)
            text.Append(CultureInfo.InvariantCulture, $"- **WIP in {MarkdownText.Inline(step.Name)}:** {Wip(step)}\n");
        if (card is { IsBlocked: true, BlockedReason: { } reason })
            text.Append(CultureInfo.InvariantCulture, $"- **Blocked:** {MarkdownText.Inline(reason)}\n");
        if (link.ClosedInGitHub)
            text.Append("- **Closed here, still in flow there:** the team finishes or cancels the card in KanbanHub.\n");
        if (link.SyncProblem is { } problem)
            text.Append(CultureInfo.InvariantCulture, $"- **Not applied:** {MarkdownText.Inline(problem)}\n");
        if (boardUrl is not null)
            text.Append(CultureInfo.InvariantCulture, $"\n[Open the board]({boardUrl})\n");

        return text.ToString();
    }

    /// <summary>The names the board itself uses: the Track sub-column is "Blocked".</summary>
    public static string NameOf(Location location) => location switch
    {
        Location.Track => "Blocked",
        Location.Parked => "Parking lot",
        _ => location.ToString()
    };

    private static string Place(SyncedCard card) => card switch
    {
        { IsFinished: true, Step: { } step } => $"{MarkdownText.Inline(step.Name)} · Done (finished)",
        { Step: { } step } when card.Location.IsInsideStep() => $"{MarkdownText.Inline(step.Name)} · {NameOf(card.Location)}",
        _ => NameOf(card.Location)
    };

    private static string Wip(SyncedStep step) =>
        step.WipLimit is { } limit ? $"{step.WipCount} of {limit}" : $"{step.WipCount}, no limit";
}

/// <summary>The "Kanban done rule" check run: the card's done-rule items and whether the required ones are ticked.</summary>
public static class DoneRuleSummary
{
    public static CheckRun For(string headSha, int cardNumber, IReadOnlyList<ChecklistLine> checklist)
    {
        bool passed = checklist.All(item => item.IsChecked || !item.IsRequired);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Done rule of KanbanHub card #{cardNumber}: {(passed ? "met" : "not met yet")}.\n");
        if (checklist.Count == 0) text.Append("\nThe card's step has no done rule.");
        foreach (ChecklistLine item in checklist)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"\n- [{(item.IsChecked ? 'x' : ' ')}] {MarkdownText.Inline(item.Text)}{(item.IsRequired ? "" : " (optional)")}");
        }

        return new CheckRun(headSha, passed, text.ToString());
    }
}

/// <summary>Finds the issues a pull request says it closes ("Fixes #42"), the way GitHub reads its own keywords.</summary>
public static partial class ClosingKeywords
{
    public static IReadOnlyList<int> FindIssueNumbers(string? pullRequestBody) =>
        pullRequestBody is null
            ? []
            : [.. Keyword().Matches(pullRequestBody).Select(NumberOf).Distinct()];

    private static int NumberOf(Match match) => int.Parse(match.Groups["number"].ValueSpan, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+#(?<number>\d{1,9})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Keyword();
}
