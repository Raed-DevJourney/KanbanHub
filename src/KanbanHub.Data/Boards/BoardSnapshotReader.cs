using KanbanHub.Contracts;
using KanbanHub.Data.Cards;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Boards;

/// <summary>Reads the five result sets of the board snapshot and assembles them into one contract.</summary>
internal static class BoardSnapshotReader
{
    /// <summary>
    /// Board, steps with limits, current done rules, cards, color overrides. Canceled cards are left out, and so are
    /// finished cards past the board's archive window (FR-B08); both still count in metrics.
    /// </summary>
    public const string Sql = $"""
        SELECT b.BoardId, b.WorkspaceId, b.[Name], b.WorkflowVersion, b.ReadyListCap, b.AgentConcurrencyLimit, b.AiEnabled
        FROM kb.Board b
        WHERE b.BoardId = @BoardId AND b.IsArchived = 0;

        SELECT s.StepId, s.[Name], s.Color, s.HasTrack, s.IsFinal, s.CountsParentsOnly, w.MaxItems, w.[Policy], s.IsAgentEligible
        FROM kb.BoardStep s
        LEFT JOIN kb.WipLimit w ON w.StepId = s.StepId AND w.Scope = 1 AND w.SwimlaneId IS NULL
        WHERE s.BoardId = @BoardId AND s.IsRetired = 0 AND s.SwimlaneId IS NULL
        ORDER BY s.SortOrder;

        SELECT r.StepId, i.[Text], i.IsRequired
        FROM kb.DoneRule r
        JOIN kb.DoneRuleItem i ON i.DoneRuleId = r.DoneRuleId
        JOIN kb.BoardStep s ON s.StepId = r.StepId
        WHERE s.BoardId = @BoardId AND r.IsCurrent = 1 AND r.CardType IS NULL
        ORDER BY i.SortOrder;

        SELECT {CardRow.SummaryColumns}
        FROM {CardRow.Source}
        JOIN kb.Board b ON b.BoardId = c.BoardId
        LEFT JOIN kb.BoardStep s ON s.StepId = c.StepId
        WHERE c.BoardId = @BoardId AND c.IsDeleted = 0 AND c.[Location] <> 5
          AND NOT (c.[Location] = 2 AND s.IsFinal = 1
                   AND c.EnteredLocationAtUtc < DATEADD(DAY, -b.ArchiveAfterDays, @NowUtc))
        ORDER BY c.[Rank];

        SELECT TokenKey, Color FROM kb.PaletteColor WHERE BoardId = @BoardId;
        """;

    private sealed record BoardRow(Guid Id, Guid WorkspaceId, string Name, int WorkflowVersion, short? ReadyListCap, byte AgentLimit)
    {
        public bool AiEnabled { get; init; }
    }

    private sealed record RuleRow(Guid StepId, DoneRuleItemDto Item);

    public static async Task<BoardSnapshotDto?> ReadAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken)) return null;
        BoardRow board = ReadBoard(reader);

        List<StepDto> steps = await NextAsync(reader, ReadStep, cancellationToken);
        List<RuleRow> rules = await NextAsync(reader, ReadRule, cancellationToken);
        List<CardDto> cards = await NextAsync(reader, CardRow.ReadSummary, cancellationToken);
        List<PaletteColor> colors = await NextAsync(reader, r => new PaletteColor(r.GetString(0), r.GetString(1)), cancellationToken);

        return new BoardSnapshotDto
        {
            Id = board.Id,
            WorkspaceId = board.WorkspaceId,
            Name = board.Name,
            WorkflowVersion = board.WorkflowVersion,
            ReadyListCap = board.ReadyListCap,
            AgentConcurrencyLimit = board.AgentLimit,
            AiEnabled = board.AiEnabled,
            Steps = [.. steps.Select((step, index) => Complete(step, index + 1, rules, cards))],
            Cards = cards,
            Palette = Palette.Effective(colors)
        };
    }

    private static async Task<List<T>> NextAsync<T>(
        SqlDataReader reader, Func<SqlDataReader, T> map, CancellationToken cancellationToken)
    {
        await reader.NextResultAsync(cancellationToken);
        List<T> rows = [];
        while (await reader.ReadAsync(cancellationToken)) rows.Add(map(reader));
        return rows;
    }

    private static BoardRow ReadBoard(SqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.Int16OrNull(4), reader.GetByte(5))
        {
            AiEnabled = reader.GetBoolean(6)
        };

    private static StepDto ReadStep(SqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Name = reader.GetString(1),
        Order = 0,
        Color = reader.GetString(2),
        HasTrack = reader.GetBoolean(3),
        IsFinal = reader.GetBoolean(4),
        CountsParentsOnly = reader.GetBoolean(5),
        WipLimit = reader.Int16OrNull(6),
        Policy = (WipPolicy?)reader.ByteOrNull(7),
        IsAgentEligible = reader.GetBoolean(8)
    };

    private static RuleRow ReadRule(SqlDataReader reader) =>
        new(reader.GetGuid(0), new DoneRuleItemDto(reader.GetString(1), reader.GetBoolean(2)));

    /// <summary>Gives a step its place in the workflow, its done rule and its current WIP count.</summary>
    private static StepDto Complete(StepDto step, int order, List<RuleRow> rules, List<CardDto> cards) => step with
    {
        Order = order,
        DoneRule = [.. rules.Where(rule => rule.StepId == step.Id).Select(rule => rule.Item)],
        WipCount = CountWip(step, cards)
    };

    /// <summary>The same count the procedure enforces, from the domain's model of rules R1 and R2.</summary>
    private static int CountWip(StepDto step, List<CardDto> cards)
    {
        var rules = new WorkflowStep(step.Id, step.Name) { IsFinal = step.IsFinal, CountsParentsOnly = step.CountsParentsOnly };
        IEnumerable<StepOccupant> occupants = cards
            .Where(card => card.StepId == step.Id)
            .Select(card => new StepOccupant(card.Id, card.ParentId, card.Location));
        return new StepWip(rules, occupants).Count;
    }
}
