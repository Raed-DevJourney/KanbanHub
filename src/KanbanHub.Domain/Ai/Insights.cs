using System.Security.Claims;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Ai;

/// <summary>A change to one step's WIP limit that the advisor proposes. Nothing changes until a person accepts it.</summary>
public sealed record class WipProposal(Guid StepId, string StepName, int CurrentLimit, int ProposedLimit);

/// <summary>
/// Something the AI noticed about a board, in one shape for every kind: what it says, the points behind it, and
/// the cards it is about. Card titles in it come from the board's own data, never from the model.
/// </summary>
public sealed record class Insight(AiFeature Kind, string Title, string Summary)
{
    public IReadOnlyList<string> Points { get; init; } = [];

    /// <summary>The numbers of the cards the insight cites; every one was in the data the model was given.</summary>
    public IReadOnlyList<int> Cards { get; init; } = [];

    public WipProposal? Proposal { get; init; }
}

public sealed record class StoredInsight(long Id, Guid BoardId, Insight Content, DateTimeOffset CreatedAt);

/// <summary>How a person closed an insight. Stored in <c>kb.AiInsight.State</c>, next to 1 (open) and 4 (superseded).</summary>
public enum InsightResolution : byte
{
    Dismissed = 2,
    Accepted = 3
}

/// <summary>Storage port for insights (<c>kb.AiInsight</c>).</summary>
public interface IInsightStore
{
    /// <summary>Makes these the board's open insights of that kind; the ones they replace are superseded.</summary>
    Task ReplaceAsync(Guid boardId, AiFeature kind, IReadOnlyList<Insight> insights, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredInsight>> ListOpenAsync(Guid boardId, CancellationToken cancellationToken);

    Task<StoredInsight?> FindOpenAsync(Guid boardId, long insightId, CancellationToken cancellationToken);

    Task ResolveAsync(long insightId, InsightResolution resolution, Stamp stamp, CancellationToken cancellationToken);
}

/// <summary>
/// What people do with insights: read them, dismiss them, and accept a proposed WIP change. Accepting is the
/// only way a suggestion turns into a change, and it runs through the same use case, permission and audit trail
/// as setting the limit by hand (the acceptance scenario "AI suggests, never acts").
/// </summary>
public sealed class InsightDesk(
    IInsightStore insights, IFlowFacts facts, BoardSettings settings, IBoardAuthorizer authorizer, TimeProvider clock)
{
    private static readonly NotFound NoSuchInsight = new("insight");

    public async Task<Outcome<IReadOnlyList<StoredInsight>>> ListAsync(Guid boardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.InsightsView, cancellationToken) is { } denied) return denied;

        return new Outcome<IReadOnlyList<StoredInsight>>(await insights.ListOpenAsync(boardId, cancellationToken));
    }

    /// <summary>Hides an insight for everyone, so it is for the people who look after the board's flow.</summary>
    public async Task<Outcome<Saved>> DismissAsync(Guid boardId, long insightId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.WipSet, cancellationToken) is { } denied) return denied;
        if (await insights.FindOpenAsync(boardId, insightId, cancellationToken) is null) return NoSuchInsight;

        await insights.ResolveAsync(insightId, InsightResolution.Dismissed, StampFor(user), cancellationToken);
        return Saved.Instance;
    }

    public async Task<Outcome<Saved>> AcceptAsync(Guid boardId, long insightId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.WipSet, cancellationToken) is { } denied) return denied;
        if (await insights.FindOpenAsync(boardId, insightId, cancellationToken) is not { } insight) return NoSuchInsight;
        if (insight.Content.Proposal is not { } proposal) return new Invalid("This insight proposes no change to accept.");

        IReadOnlyList<StepLimit> limits = await facts.ListLimitsAsync(boardId, cancellationToken);
        if (limits.FirstOrDefault(limit => limit.StepId == proposal.StepId) is not { } current || current.MaxItems != proposal.CurrentLimit)
            return new Invalid($"The limit of {proposal.StepName} changed since this was suggested. Dismiss the suggestion.");

        Outcome<Saved> applied = await settings.SetWipLimitsAsync(ChangeFor(boardId, limits, proposal), user, cancellationToken);
        if (applied.Value is Saved)
            await insights.ResolveAsync(insightId, InsightResolution.Accepted, StampFor(user), cancellationToken);
        return applied;
    }

    /// <summary>Setting limits replaces all of a board's limits, so the other steps keep theirs by being listed.</summary>
    private static WipLimitChange ChangeFor(Guid boardId, IReadOnlyList<StepLimit> limits, WipProposal proposal) => new(
        boardId,
        [.. limits.Select(limit => limit.StepId == proposal.StepId ? limit with { MaxItems = proposal.ProposedLimit } : limit)],
        $"Accepted the AI advisor's suggestion for {proposal.StepName}: {proposal.CurrentLimit} to {proposal.ProposedLimit}.");

    private Stamp StampFor(ClaimsPrincipal user) => new(user.GetObjectId(), clock.GetUtcNow());
}
