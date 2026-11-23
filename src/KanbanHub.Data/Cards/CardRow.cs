using KanbanHub.Contracts;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Cards;

/// <summary>The one place that knows which card columns the read queries select, and in what order.</summary>
internal static class CardRow
{
    /// <summary>
    /// What the read queries select cards from: the card as <c>c</c>, with its GitHub issue when it has one and
    /// the agent session that is open on it (at most one: a unique index sees to that).
    /// </summary>
    public const string Source = """
        kb.Card c
        LEFT JOIN kb.GitHubIssueLink gl ON gl.CardId = c.CardId
        LEFT JOIN kb.GitHubRepoLink gr ON gr.RepositoryId = gl.RepositoryId
        LEFT JOIN kb.AgentDispatch ad ON ad.CardId = c.CardId AND ad.[State] IN (1, 2)
        """;

    /// <summary>Columns of a card summary, for a query that selects from <see cref="Source"/>.</summary>
    public const string SummaryColumns = """
        c.CardId, c.CardNumber, c.Title, c.CardType, c.PriorityBucket, c.Origin, c.[Location], c.StepId, c.[Rank],
        c.IsReady, c.ParentCardId, c.IsContainer, c.IsBlocked, c.BlockedReason, c.DueDate, c.EnteredLocationAtUtc,
        CAST(c.[RowVersion] AS binary(8)), c.ParkedFromStepId, gr.FullName, gl.IssueNumber, gl.SyncProblem, gl.ClosedInGitHub,
        ad.Agent, ad.[State], ad.PullRequestNumber
        """;

    /// <summary>The summary columns followed by what only the card panel shows.</summary>
    public const string DetailColumns = SummaryColumns + """
        , c.BoardId, c.CreatedBy, c.[Description], c.TargetRelease, c.EstimatedTasks, c.ActualTasks,
        c.BlockerCategory, c.CanceledReason
        """;

    public static CardDto ReadSummary(SqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Number = reader.GetInt32(1),
        Title = reader.GetString(2),
        CardType = (CardType)reader.GetByte(3),
        PriorityBucket = reader.ByteOrNull(4),
        Origin = reader.StringOrNull(5),
        Location = (Location)reader.GetByte(6),
        StepId = reader.GuidOrNull(7),
        Rank = reader.GetDecimal(8),
        IsReady = reader.GetBoolean(9),
        ParentId = reader.GuidOrNull(10),
        IsContainer = reader.GetBoolean(11),
        IsBlocked = reader.GetBoolean(12),
        BlockedReason = reader.StringOrNull(13),
        DueDate = reader.DateOrNull(14),
        EnteredLocationAtUtc = reader.UtcDateTime(15),
        ETag = Convert.ToBase64String(reader.GetFieldValue<byte[]>(16)),
        ParkedFromStepId = reader.GuidOrNull(17),
        Issue = ReadIssue(reader),
        Agent = ReadAgent(reader)
    };

    private static GitHubIssueDto? ReadIssue(SqlDataReader reader) =>
        reader.IsDBNull(19)
            ? null
            : new GitHubIssueDto(reader.StringOrNull(18) ?? "", reader.GetInt32(19))
            {
                SyncProblem = reader.StringOrNull(20),
                ClosedInGitHub = reader.GetBoolean(21)
            };

    private static CardAgentDto? ReadAgent(SqlDataReader reader) =>
        reader.IsDBNull(22)
            ? null
            : new CardAgentDto(reader.GetString(22), (DispatchState)reader.GetByte(23), reader.IsDBNull(24) ? null : reader.GetInt32(24));

    public static CardDetailDto ReadDetail(SqlDataReader reader) => new()
    {
        Card = ReadSummary(reader),
        BoardId = reader.GetGuid(25),
        CreatedBy = reader.GetGuid(26),
        Description = reader.StringOrNull(27),
        TargetRelease = reader.StringOrNull(28),
        EstimatedTasks = reader.Int16OrNull(29),
        ActualTasks = reader.Int16OrNull(30),
        BlockerCategory = (BlockerCategory?)reader.ByteOrNull(31),
        CanceledReason = reader.StringOrNull(32)
    };
}
