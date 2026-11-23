using KanbanHub.Domain.Flow;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Cards;

/// <summary>Turns the result sets of <c>kb.usp_MoveCard</c> into a <see cref="MoveOutcome"/>.</summary>
internal static class MoveOutcomeReader
{
    /// <summary>The first column of the procedure's first result set.</summary>
    private enum ResultCode
    {
        Moved = 0,
        WipLimitReached = 1,
        ReasonRequired = 2,
        DoneRuleUnmet = 3,
        Conflict = 4,
        IllegalTarget = 5,
        NotFound = 6
    }

    public static async Task<MoveOutcome> ReadAsync(SqlDataReader reader, Guid cardId, CancellationToken cancellationToken)
    {
        await reader.ReadAsync(cancellationToken);

        return (ResultCode)reader.GetInt32(0) switch
        {
            ResultCode.Moved => await ReadMovedAsync(reader, cardId, cancellationToken),
            ResultCode.WipLimitReached => new WipLimitReached(await ReadViolationsAsync(reader, cancellationToken), SwarmCandidates: []),
            ResultCode.ReasonRequired => new OverrideReasonRequired(await ReadViolationsAsync(reader, cancellationToken)),
            ResultCode.DoneRuleUnmet => new DoneRuleUnmet(await ReadUnmetItemsAsync(reader, cancellationToken)),
            ResultCode.Conflict => new ConcurrencyConflict(reader.GetFieldValue<byte[]>(1)),
            ResultCode.IllegalTarget => IllegalTransition.UnknownColumn,
            ResultCode.NotFound => throw new CardNotFoundException(cardId),
            var code => throw new InvalidOperationException($"Unknown result code {code} from kb.usp_MoveCard.")
        };
    }

    private static async Task<Moved> ReadMovedAsync(SqlDataReader reader, Guid cardId, CancellationToken cancellationToken)
    {
        long movementId = reader.GetInt64(1);
        Guid boardId = reader.GetGuid(2);
        byte[] rowVersion = reader.GetFieldValue<byte[]>(3);
        return new Moved(cardId, boardId, movementId, rowVersion, await ReadViolationsAsync(reader, cancellationToken));
    }

    private static async Task<IReadOnlyList<WipViolation>> ReadViolationsAsync(
        SqlDataReader reader, CancellationToken cancellationToken)
    {
        List<WipViolation> violations = [];
        if (!await reader.NextResultAsync(cancellationToken)) return violations;

        while (await reader.ReadAsync(cancellationToken))
        {
            violations.Add(new WipViolation(
                Scope: (WipScope)reader.GetByte(0),
                ScopeName: reader.GetString(1),
                Limit: reader.GetInt32(2),
                Current: reader.GetInt32(3),
                Policy: (WipPolicy)reader.GetByte(4)));
        }

        return violations;
    }

    private static async Task<IReadOnlyList<string>> ReadUnmetItemsAsync(
        SqlDataReader reader, CancellationToken cancellationToken)
    {
        List<string> items = [];
        if (!await reader.NextResultAsync(cancellationToken)) return items;

        while (await reader.ReadAsync(cancellationToken)) items.Add(reader.GetString(0));
        return items;
    }
}
