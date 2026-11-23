using KanbanHub.Contracts;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using KanbanHub.Web.Api;

namespace KanbanHub.Web.State;

/// <summary>How a move looks from the browser before the server has spoken. A preview only: the server decides.</summary>
public sealed record MovePreview(bool IsLegal, bool IsFull, bool NeedsReason)
{
    public bool IsAllowed => IsLegal && !IsFull;
}

public enum MoveResult
{
    Moved,
    Rejected,
    ReasonRequired
}

/// <summary>
/// The board on screen: its snapshot, what the user may do on it, and optimistic moves that
/// snap back with an explanation when the server refuses them (FR-M05).
/// </summary>
public sealed class BoardStore(KanbanApi api, Notices notices)
{
    private const string ReasonRequiredKind = "override-reason-required";

    private List<CardDto> cards = [];
    private bool isBehind;
    private bool isCatchingUp;

    public event Action? Changed;

    public BoardSnapshotDto? Board { get; private set; }

    public Permission Granted { get; private set; }

    /// <summary>Why the board could not be loaded, when it could not.</summary>
    public ApiProblem? LoadProblem { get; private set; }

    public IReadOnlyList<StepDto> Steps => Board?.Steps ?? [];

    public IReadOnlyList<CardDto> Cards => cards;

    public bool Can(Permission permission) => Granted.HasFlag(permission);

    /// <summary>The AI buttons appear only on a board that opted in, for people who may use AI.</summary>
    public bool CanUseAi => Board?.AiEnabled == true && Can(Permission.AiUse);

    public async Task LoadAsync(Guid boardId, CancellationToken cancellationToken)
    {
        Granted = await api.GetBoardAccessAsync(boardId, cancellationToken) is { Value: BoardAccessResponse access }
            ? ParsePermissions(access.Permissions)
            : Permission.None;
        await ReloadAsync(boardId, cancellationToken);
    }

    public Task ReloadAsync(CancellationToken cancellationToken) =>
        Board is null ? Task.CompletedTask : ReloadAsync(Board.Id, cancellationToken);

    /// <summary>
    /// Reloads because someone else changed the board. Changes that arrive while a reload is running share
    /// one more reload, so the snapshot on screen is never older than the last change heard of.
    /// </summary>
    public async Task CatchUpAsync(CancellationToken cancellationToken)
    {
        isBehind = true;
        if (isCatchingUp) return;

        isCatchingUp = true;
        try
        {
            while (isBehind)
            {
                isBehind = false;
                await ReloadAsync(cancellationToken);
            }
        }
        finally
        {
            isCatchingUp = false;
        }
    }

    public CardDto? Find(Guid cardId) => cards.Find(card => card.Id == cardId);

    public IEnumerable<CardDto> CardsAt(BoardSlot slot) => cards.Where(slot.Holds).OrderBy(card => card.Rank);

    /// <summary>The step's WIP as rules R1 and R2 count it, from the cards on screen.</summary>
    public int WipCountOf(StepDto step) => LoadOf(step).Count;

    public MovePreview Preview(CardDto card, BoardSlot target)
    {
        target = Current(target);
        var plan = new MovePlan(PositionOf(card), target.ToPosition());
        WipViolation? violation = FindViolation(card, target);

        return new MovePreview(
            IsLegal: plan.FindIllegality(hasReason: true) is null && !target.Holds(card),
            IsFull: violation is { Policy: WipPolicy.Strict } || (violation is { Policy: WipPolicy.AllowWithReason } && !Can(Permission.WipOverride)),
            NeedsReason: plan.IsBackward || violation is { Policy: WipPolicy.AllowWithReason });
    }

    /// <summary>Moves the card on screen at once, then asks the server; a refusal puts the card back.</summary>
    public async Task<MoveResult> MoveAsync(CardDto card, BoardSlot target, string? reason, CancellationToken cancellationToken)
    {
        if (reason is null && Preview(card, target).NeedsReason) return MoveResult.ReasonRequired;

        Replace(card, card with { Location = target.Location, StepId = target.StepId });
        var body = new MoveCardBody(target.StepId, target.Location, OverrideReason: reason);
        ApiResult<Moved> result = await api.MoveCardAsync(card.Id, card.ETag, body, cancellationToken);

        if (result.Value is ApiProblem problem) return Reject(card, target, problem);

        notices.Announce($"Moved {card.Title} to {target.Label}. {DescribeWip(target)}");
        await ReloadAsync(cancellationToken);
        return MoveResult.Moved;
    }

    public string DescribeWip(BoardSlot slot) => Current(slot).Step switch
    {
        null => "",
        { WipLimit: null } step => $"WIP {WipCountOf(step)}, no limit.",
        { } step => $"WIP {WipCountOf(step)} of {step.WipLimit}."
    };

    /// <summary>The slot with the step as the board holds it now, whatever copy the caller kept.</summary>
    private BoardSlot Current(BoardSlot slot) =>
        Steps.FirstOrDefault(step => step.Id == slot.StepId) is { } step ? slot with { Step = step } : slot;

    private async Task ReloadAsync(Guid boardId, CancellationToken cancellationToken)
    {
        ApiResult<BoardSnapshotDto> result = await api.GetBoardAsync(boardId, cancellationToken);
        LoadProblem = result.Value as ApiProblem;
        if (result.Value is BoardSnapshotDto snapshot)
        {
            Board = snapshot;
            cards = [.. snapshot.Cards];
        }

        Changed?.Invoke();
    }

    private MoveResult Reject(CardDto card, BoardSlot target, ApiProblem problem)
    {
        Replace(card with { Location = target.Location, StepId = target.StepId }, card);
        if (problem.Kind == ReasonRequiredKind) return MoveResult.ReasonRequired;

        notices.Show(NoticeKind.Problem, problem.Detail ?? "The move was rejected.", [.. SwarmHints(problem), .. problem.UnmetItems]);
        return MoveResult.Rejected;
    }

    /// <summary>"Stop starting, start finishing": name the cards in the full step that could use help.</summary>
    private IEnumerable<string> SwarmHints(ApiProblem problem) =>
        problem.SwarmCandidates
            .Select(Find)
            .OfType<CardDto>()
            .Select(card => card.IsBlocked ? $"#{card.Number} {card.Title} (blocked)" : $"#{card.Number} {card.Title}");

    private void Replace(CardDto current, CardDto replacement)
    {
        int index = cards.FindIndex(card => card.Id == current.Id);
        if (index >= 0) cards[index] = replacement;
        Changed?.Invoke();
    }

    private WipViolation? FindViolation(CardDto card, BoardSlot target)
    {
        if (target is not { Step: { WipLimit: { } max, Policy: { } policy } step, Location: Location.Active }) return null;

        var limit = new WipLimit(WipLimit.Clamp(max), policy);
        return LoadOf(step).FindViolation(limit, new StepOccupant(card.Id, card.ParentId, card.Location));
    }

    private StepWip LoadOf(StepDto step)
    {
        var rules = new WorkflowStep(step.Id, step.Name) { IsFinal = step.IsFinal, CountsParentsOnly = step.CountsParentsOnly };
        IEnumerable<StepOccupant> occupants = cards
            .Where(card => card.StepId == step.Id)
            .Select(card => new StepOccupant(card.Id, card.ParentId, card.Location));
        return new StepWip(rules, occupants);
    }

    private CardPosition PositionOf(CardDto card) =>
        Steps.FirstOrDefault(step => step.Id == card.StepId) is { } step && card.Location.IsInsideStep()
            ? CardPosition.InStep(step.Id, step.Order, card.Location)
            : CardPosition.OutsideSteps(card.Location, card.ParkedFromStepId);

    private static Permission ParsePermissions(IEnumerable<string> names) =>
        names.Aggregate(Permission.None, (all, name) => Enum.TryParse(name, out Permission one) ? all | one : all);
}
