using System.Security.Claims;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Planning;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Boards;

/// <summary>Changes a board's policies: WIP limits, done rules and who holds which role.</summary>
public sealed class BoardSettings(IBoardStore store, IBoardAuthorizer authorizer, TimeProvider clock)
{
    public const int MaxDoneRuleItemLength = 300;

    private static readonly NotFound UnknownStep = new("step");

    public async Task<Outcome<Saved>> SetWipLimitsAsync(
        WipLimitChange change, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, change.BoardId, Permission.WipSet, cancellationToken) is { } denied)
            return denied;
        if (FindProblems(change.Limits) is { Count: > 0 } problems)
            return new Invalid(problems);

        bool stored = await store.SetWipLimitsAsync(change, StampFor(user), cancellationToken);
        return stored ? Saved.Instance : UnknownStep;
    }

    /// <summary>The WIP calculator: a preview of the book's starting limits. Nothing is saved.</summary>
    public async Task<Outcome<IReadOnlyList<WipSuggestion>>> SuggestWipLimitsAsync(
        Guid boardId, IReadOnlyList<StepCapacityDraft> capacities, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.WipSet, cancellationToken) is { } denied)
            return denied;
        if (capacities.Count == 0 || capacities.Any(capacity => !capacity.IsUsable))
            return new Invalid("Every step needs a name, a rate above zero and at least one person.");

        return new Outcome<IReadOnlyList<WipSuggestion>>(
            WipCalculator.Suggest([.. capacities.Select(capacity => capacity.ToCapacity())]));
    }

    public async Task<Outcome<Saved>> SetDoneRuleAsync(
        DoneRuleChange change, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, change.BoardId, Permission.DoneRuleEdit, cancellationToken) is { } denied)
            return denied;
        if (change.Items.Any(item => string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > MaxDoneRuleItemLength))
            return new Invalid($"Each done-rule item needs 1 to {MaxDoneRuleItemLength} characters of text.");

        bool stored = await store.SetDoneRuleAsync(change, StampFor(user), cancellationToken);
        return stored ? Saved.Instance : UnknownStep;
    }

    public async Task<Outcome<Saved>> SetMembersAsync(
        Guid boardId, IReadOnlyList<MemberAssignment> members, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.BoardMembersManage, cancellationToken) is { } denied)
            return denied;
        if (members.Any(member => BuiltInRoles.Find(member.RoleId) is null))
            return new Invalid("Every member needs one of the built-in roles.");
        if (members.All(member => member.RoleId != BuiltInRoles.Owner.Id))
            return new Invalid("A board needs at least one Owner.");

        await store.SetMembersAsync(boardId, members, StampFor(user), cancellationToken);
        return Saved.Instance;
    }

    private Stamp StampFor(ClaimsPrincipal user) => new(user.GetObjectId(), clock.GetUtcNow());

    /// <summary>R8: limits run from 1 to 99 and carry a known policy.</summary>
    private static List<string> FindProblems(IReadOnlyList<StepLimit> limits) =>
    [
        .. limits
            .Where(limit => limit.MaxItems is < WipLimit.Minimum or > WipLimit.Maximum || !Enum.IsDefined(limit.Policy))
            .Select(limit => $"The limit for step {limit.StepId} must be {WipLimit.Minimum} to {WipLimit.Maximum} with a known policy.")
    ];
}

/// <summary>A calculator input as a caller submits it; checked before it becomes a <see cref="StepCapacity"/>.</summary>
public sealed record class StepCapacityDraft(string StepName, decimal ItemsPerPerson, int People)
{
    public bool IsUsable => !string.IsNullOrWhiteSpace(StepName) && ItemsPerPerson > 0 && People > 0;

    public StepCapacity ToCapacity() => new(StepName, ItemsPerPerson, People);
}
