namespace KanbanHub.Domain.GitHub;

/// <summary><c>/kanban move next</c>: one column forward.</summary>
public sealed record class MoveNext;

/// <summary><c>/kanban block &lt;reason&gt;</c></summary>
public sealed record class BlockCard(string Reason);

/// <summary><c>/kanban unblock</c></summary>
public sealed record class UnblockCard;

/// <summary><c>/kanban track</c>: set the card aside in its step's Blocked sub-column without flagging it.</summary>
public sealed record class TrackCard;

/// <summary>A <c>/kanban</c> line the app didn't understand; it answers with what it does understand.</summary>
public sealed record class ShowHelp;

/// <summary>A command given in an issue comment (section 8.3).</summary>
public union SlashCommand(MoveNext, BlockCard, UnblockCard, TrackCard, ShowHelp);

public static class SlashCommands
{
    public const string Prefix = "/kanban";

    public const string Help =
        "KanbanHub commands: `/kanban move next`, `/kanban block <reason>`, `/kanban unblock`, `/kanban track`.";

    /// <summary>The command a comment gives on its first line, or null when the comment is just a comment.</summary>
    public static SlashCommand? TryParse(string commentBody)
    {
        string firstLine = commentBody.TrimStart().Split('\n', 2)[0].Trim();
        if (!firstLine.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;

        string[] words = firstLine[Prefix.Length..]
            .Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : "";
        string rest = words.Length > 1 ? words[1] : "";

        return Parse(verb, rest);
    }

    private static SlashCommand Parse(string verb, string rest) => (verb, rest.ToLowerInvariant()) switch
    {
        ("move", "next") => new MoveNext(),
        ("block", not "") => new BlockCard(rest),
        ("unblock", "") => new UnblockCard(),
        ("track", "") => new TrackCard(),
        _ => new ShowHelp()
    };
}
