using System.Security.Cryptography;
using System.Text;

namespace KanbanHub.Domain.GitHub;

/// <summary>The fields the ledger keeps track of.</summary>
public static class SyncFields
{
    public const string Title = "title";
    public const string Body = "body";
    public const string Labels = "labels";
    public const string State = "state";
    public const string StatusComment = "status-comment";

    public static string CheckRunOf(int pullNumber) => $"check-run:{pullNumber}";
}

/// <summary>
/// The echo and conflict rules of the sync (sections 9.3 and 9.4), decided from the ledger alone:
/// a value that was the last one synced, in either direction, is never sent again.
/// </summary>
public static class FieldSync
{
    /// <summary>SHA-256 of the value as both sides would agree on it: no surrounding space, one kind of line ending.</summary>
    public static byte[] Hash(string? value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes((value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim()));

    /// <summary>Outbound: write when KanbanHub's value differs from what was last synced.</summary>
    public static bool ShouldPush(string? value, LedgerEntry? last) => last is null || !Matches(value, last);

    /// <summary>
    /// Inbound: apply GitHub's value unless it is what was last synced (an echo, or no change),
    /// or KanbanHub wrote a different value after GitHub's was made (last writer wins).
    /// </summary>
    public static bool ShouldApply(string? value, DateTimeOffset updatedInGitHub, LedgerEntry? last) =>
        last is null
        || (!Matches(value, last) && !(last.Direction is SyncDirection.Outbound && last.SourceUpdated > updatedInGitHub));

    /// <summary>
    /// An issue is reopened only by the side that closed it: when the card comes back into flow,
    /// the issue reopens if KanbanHub's own sync was what closed it.
    /// </summary>
    public static bool ShouldReopen(LedgerEntry? last) =>
        last is { Direction: SyncDirection.Outbound } && !Matches(nameof(IssueState.Open), last);

    private static bool Matches(string? value, LedgerEntry entry) => Hash(value).AsSpan().SequenceEqual(entry.ValueHash);
}

/// <summary>The ledger with a clock: what was last synced per field, and a record of what is synced now.</summary>
public sealed class SyncJournal(ISyncLedger ledger, TimeProvider clock)
{
    public Task<LedgerEntry?> LastAsync(Guid cardId, string field, CancellationToken cancellationToken) =>
        ledger.FindLastAsync(cardId, field, cancellationToken);

    public async Task<bool> IsNewsAsync(Guid cardId, string field, string? value, CancellationToken cancellationToken) =>
        FieldSync.ShouldPush(value, await ledger.FindLastAsync(cardId, field, cancellationToken));

    public async Task<bool> ShouldApplyAsync(Guid cardId, string field, string? value, DateTimeOffset updatedInGitHub, CancellationToken cancellationToken) =>
        FieldSync.ShouldApply(value, updatedInGitHub, await ledger.FindLastAsync(cardId, field, cancellationToken));

    public Task RecordAsync(Guid cardId, SyncDirection direction, string field, string? value, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        return ledger.RecordAsync(cardId, new LedgerEntry(direction, field, FieldSync.Hash(value), now), now, cancellationToken);
    }
}
