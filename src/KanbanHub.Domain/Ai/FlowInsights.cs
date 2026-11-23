using System.Security.Claims;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Planning;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Ai;

/// <summary>A step's flow, counted in SQL before any model sees it (section 11.2: grounding data is pre-aggregated).</summary>
public sealed record class StepFlow(Guid StepId, string Name, int Order)
{
    public bool IsFinal { get; init; }

    public int ActiveCount { get; init; }

    public int DoneCount { get; init; }

    public int BlockedCount { get; init; }

    public int? WipLimit { get; init; }

    /// <summary>Days the card that has waited longest in this step's Done has waited for a pull.</summary>
    public int OldestDoneDays { get; init; }

    /// <summary>Cards that reached this step's Done in the last four weeks.</summary>
    public int FinishedLast28Days { get; init; }

    /// <summary>Moves into this step that went over its limit in the last four weeks.</summary>
    public int OverridesLast28Days { get; init; }
}

/// <summary>A card in one of the snapshot's lists; what <see cref="Days"/> counts depends on the list.</summary>
public sealed record class FlowCard(int Number, string Title, string Step, int Days)
{
    public string? Reason { get; init; }
}

/// <summary>What a board looks like right now, for the standup brief, the bottleneck finder and the WIP advisor.</summary>
public sealed record class FlowSnapshot
{
    public required IReadOnlyList<StepFlow> Steps { get; init; }

    /// <summary>Cards that reached the last step's Done in the last 24 hours.</summary>
    public IReadOnlyList<FlowCard> Finished { get; init; } = [];

    /// <summary>Blocked cards, with the days they have been blocked and why.</summary>
    public IReadOnlyList<FlowCard> Blocked { get; init; } = [];

    /// <summary>Cards that have sat in an Active column longest, with their days there.</summary>
    public IReadOnlyList<FlowCard> Aging { get; init; } = [];

    /// <summary>Cards waiting in a Done column for the next step to pull them, with their days waiting.</summary>
    public IReadOnlyList<FlowCard> Waiting { get; init; } = [];

    public IEnumerable<FlowCard> Cards => Finished.Concat(Blocked).Concat(Aging).Concat(Waiting);

    public FlowCard? Find(int number) => Cards.FirstOrDefault(card => card.Number == number);

    /// <summary>Every number a statement about this snapshot may contain.</summary>
    public IEnumerable<decimal> Numbers =>
    [
        Steps.Count, Finished.Count, Blocked.Count, Aging.Count, Waiting.Count,
        .. Cards.Select(card => (decimal)card.Days),
        .. Steps.SelectMany(step => new decimal?[]
        {
            step.Order, step.ActiveCount, step.DoneCount, step.BlockedCount, step.WipLimit, step.OldestDoneDays,
            step.FinishedLast28Days, step.OverridesLast28Days
        }).OfType<decimal>()
    ];
}

/// <summary>Read port for flow facts. Everything here is counted by SQL from the board's own history.</summary>
public interface IFlowFacts
{
    /// <summary>Null when there is no such board.</summary>
    Task<FlowSnapshot?> FindSnapshotAsync(Guid boardId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<StepLimit>> ListLimitsAsync(Guid boardId, CancellationToken cancellationToken);

    Task<ForecastInputs> GetForecastInputsAsync(Guid boardId, byte? bucket, int days, CancellationToken cancellationToken);
}

// ---- standup brief (feature 1)

public sealed record class SwarmSuggestion(string Step, string Why);

/// <summary>The brief of section 11.2. Cards are cited by number; the board's own data supplies their titles.</summary>
public sealed record class StandupBrief(
    IReadOnlyList<int> Finished, IReadOnlyList<int> Blocked, IReadOnlyList<int> AtRisk,
    IReadOnlyList<SwarmSuggestion> Swarm, string Summary);

public sealed class StandupBriefTask : AiTask<FlowSnapshot, StandupBrief>
{
    public const int MaxSummaryLength = 600;

    public override AiFeature Feature => AiFeature.StandupBrief;

    public override string Instructions =>
        "You write a Kanban standup brief. Walk the board from the last step to the first."
        + " finished: the numbers of the cards in data.finished. blocked: the numbers of the blocked cards, longest blocked first."
        + " atRisk: numbers of cards from data.aging or data.blocked that most need attention."
        + " swarm: the step whose Done column is full or whose cards are oldest, with one sentence why."
        + " summary: three sentences at most."
        + GroundRules;

    public override bool IsGrounded(FlowSnapshot data, StandupBrief result)
    {
        int[] known = [.. data.Cards.Select(card => card.Number)];
        string[] texts = [result.Summary, .. result.Swarm.Select(swarm => swarm.Why)];

        return Grounding.CitesOnly(result.Finished, data.Finished.Select(card => card.Number))
            && Grounding.CitesOnly(result.Blocked, data.Blocked.Select(card => card.Number))
            && Grounding.CitesOnly(result.AtRisk, data.Aging.Concat(data.Blocked).Select(card => card.Number))
            && result.Swarm.All(swarm => Grounding.IsOneOf(swarm.Step, data.Steps.Select(step => step.Name)))
            && result.Summary is { Length: > 0 and <= MaxSummaryLength }
            && texts.All(text => Grounding.MentionsOnly(text, known) && Grounding.NumbersComeFrom(text, data.Numbers));
    }

    public static Insight ToInsight(FlowSnapshot data, StandupBrief brief) => new(AiFeature.StandupBrief, "Standup brief", brief.Summary)
    {
        Points =
        [
            .. Describe(data, brief.Finished, card => $"Finished: #{card.Number} {card.Title}"),
            .. Describe(data, brief.Blocked, card => $"Blocked for {card.Days} days: #{card.Number} {card.Title}{Because(card)}"),
            .. Describe(data, brief.AtRisk, card => $"At risk: #{card.Number} {card.Title}, {card.Days} days in {card.Step}"),
            .. brief.Swarm.Select(swarm => $"Swarm on {swarm.Step}: {swarm.Why}")
        ],
        Cards = [.. brief.Finished.Concat(brief.Blocked).Concat(brief.AtRisk).Distinct()]
    };

    private static IEnumerable<string> Describe(FlowSnapshot data, IEnumerable<int> numbers, Func<FlowCard, string> describe) =>
        numbers.Select(data.Find).OfType<FlowCard>().Select(describe);

    private static string Because(FlowCard card) => string.IsNullOrWhiteSpace(card.Reason) ? "" : $" ({card.Reason})";
}

// ---- bottleneck and constraint finder (feature 2)

/// <summary>
/// The book's troubleshooting rule: when cards pile up in a step's Done, the step after it isn't pulling, so that
/// next step is the constraint. Found by rule; the model only words it.
/// </summary>
public sealed record class Constraint(StepFlow ConstraintStep, StepFlow WaitingStep, IReadOnlyList<FlowCard> WaitingCards);

public static class ConstraintFinder
{
    public const int MinWaitingCards = 2;
    public const int MinWaitingDays = 2;

    public static Constraint? Find(FlowSnapshot snapshot)
    {
        StepFlow? waiting = snapshot.Steps
            .Where(step => !step.IsFinal && (step.DoneCount >= MinWaitingCards || step.OldestDoneDays >= MinWaitingDays) && step.DoneCount > 0)
            .OrderByDescending(step => step.OldestDoneDays).ThenByDescending(step => step.DoneCount)
            .FirstOrDefault();
        if (waiting is null) return null;

        StepFlow? next = snapshot.Steps.FirstOrDefault(step => step.Order == waiting.Order + 1);
        return next is null ? null : new Constraint(next, waiting, [.. snapshot.Waiting.Where(card => card.Step == waiting.Name)]);
    }
}

public sealed record class BottleneckStatement(string Statement, string Advice);

public sealed class BottleneckTask : AiTask<Constraint, BottleneckStatement>
{
    public const int MaxLength = 400;

    public override AiFeature Feature => AiFeature.Bottleneck;

    public override string Instructions =>
        "You explain a Kanban board's constraint to its team. constraintStep is the constraint: waitingStep's Done column holds"
        + " cards it isn't pulling. statement: say which step is the constraint and what shows it, naming the constraint step."
        + " advice: one sentence on how the team can help that step, the way Brechner's book suggests (swarm on it; don't start more upstream)."
        + GroundRules;

    public override bool IsGrounded(Constraint data, BottleneckStatement result)
    {
        int[] known = [.. data.WaitingCards.Select(card => card.Number)];
        decimal[] numbers = [.. NumbersOf(data)];

        return result.Statement is { Length: > 0 and <= MaxLength }
            && result.Advice is { Length: > 0 and <= MaxLength }
            && result.Statement.Contains(data.ConstraintStep.Name, StringComparison.OrdinalIgnoreCase)
            && new[] { result.Statement, result.Advice }.All(text =>
                Grounding.MentionsOnly(text, known) && Grounding.NumbersComeFrom(text, numbers));
    }

    public static Insight ToInsight(Constraint data, BottleneckStatement statement) =>
        new(AiFeature.Bottleneck, $"{data.ConstraintStep.Name} is the constraint", statement.Statement)
        {
            Points = [statement.Advice],
            Cards = [.. data.WaitingCards.Select(card => card.Number)]
        };

    private static IEnumerable<decimal> NumbersOf(Constraint data) =>
    [
        data.WaitingCards.Count,
        .. data.WaitingCards.Select(card => (decimal)card.Days),
        .. new[] { data.ConstraintStep, data.WaitingStep }.SelectMany(step => new decimal?[]
        {
            step.ActiveCount, step.DoneCount, step.BlockedCount, step.WipLimit, step.OldestDoneDays
        }).OfType<decimal>()
    ];
}

// ---- WIP tuning advisor (feature 3)

public sealed record class WipSuggestionLine(string Step, int CurrentLimit, int ProposedLimit, string Reason);

public sealed record class WipAdvice(IReadOnlyList<WipSuggestionLine> Proposals);

public sealed class WipAdviceTask : AiTask<FlowSnapshot, WipAdvice>
{
    public const int MaxProposals = 3;
    public const int MaxReasonLength = 400;

    public override AiFeature Feature => AiFeature.WipAdvice;

    public override string Instructions =>
        "You advise on a Kanban board's WIP limits, following Brechner's tuning rules: raise the limit of a step that swings"
        + " between starved and full by one; lower the limits around a slow step; lower a fast step's limit; the minimum is one;"
        + " raising a limit is a last resort. Propose at most three changes, each of exactly one, only for steps that have a"
        + " wipLimit, with currentLimit as the data states it and the reason in one or two sentences. Propose nothing when the limits fit."
        + GroundRules;

    public override bool IsGrounded(FlowSnapshot data, WipAdvice result) =>
        result.Proposals.Count <= MaxProposals
        && result.Proposals.Select(proposal => proposal.Step.Trim().ToUpperInvariant()).Distinct().Count() == result.Proposals.Count
        && result.Proposals.All(proposal => IsSound(proposal, data));

    private static bool IsSound(WipSuggestionLine proposal, FlowSnapshot data) =>
        FindStep(data, proposal.Step) is { WipLimit: { } limit }
        && proposal.CurrentLimit == limit
        && Math.Abs(proposal.ProposedLimit - limit) == 1
        && proposal.ProposedLimit is >= WipLimit.Minimum and <= WipLimit.Maximum
        && proposal.Reason is { Length: > 0 and <= MaxReasonLength }
        && Grounding.MentionsOnly(proposal.Reason, data.Cards.Select(card => card.Number))
        && Grounding.NumbersComeFrom(proposal.Reason, data.Numbers.Append(proposal.ProposedLimit));

    public static IReadOnlyList<Insight> ToInsights(FlowSnapshot data, WipAdvice advice) =>
    [
        .. advice.Proposals.Select(proposal => ToInsight(FindStep(data, proposal.Step)!, proposal))
    ];

    private static Insight ToInsight(StepFlow step, WipSuggestionLine proposal)
    {
        string verb = proposal.ProposedLimit > proposal.CurrentLimit ? "Raise" : "Lower";
        return new Insight(AiFeature.WipAdvice, $"{verb} {step.Name} from {proposal.CurrentLimit} to {proposal.ProposedLimit}", proposal.Reason)
        {
            Proposal = new WipProposal(step.StepId, step.Name, proposal.CurrentLimit, proposal.ProposedLimit)
        };
    }

    private static StepFlow? FindStep(FlowSnapshot data, string name) =>
        data.Steps.FirstOrDefault(step => string.Equals(step.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The background analysis of a board (features 1 to 3): reads the flow facts, asks the model, and stores what
/// passes the grounding check as the board's open insight of that kind. It never touches a card or a limit.
/// </summary>
public sealed class FlowAnalyst(IFlowFacts facts, IInsightStore insights, AiRunner runner, TimeProvider clock)
{
    public async Task BriefStandupAsync(Guid boardId, CancellationToken cancellationToken)
    {
        if (await facts.FindSnapshotAsync(boardId, clock.GetUtcNow(), cancellationToken) is not { } snapshot) return;
        if (!snapshot.Cards.Any()) return;   // an empty board needs no brief

        AiResult<StandupBrief> brief = await runner.RunAsync(boardId, new StandupBriefTask(), snapshot, cancellationToken);
        if (brief.Value is StandupBrief checkedBrief)
            await StoreAsync(boardId, AiFeature.StandupBrief, [StandupBriefTask.ToInsight(snapshot, checkedBrief)], cancellationToken);
    }

    /// <summary>When the rule finds no constraint, the old insight is withdrawn and the model isn't asked.</summary>
    public async Task FindBottleneckAsync(Guid boardId, CancellationToken cancellationToken)
    {
        if (await facts.FindSnapshotAsync(boardId, clock.GetUtcNow(), cancellationToken) is not { } snapshot) return;
        if (ConstraintFinder.Find(snapshot) is not { } constraint)
        {
            await StoreAsync(boardId, AiFeature.Bottleneck, [], cancellationToken);
            return;
        }

        AiResult<BottleneckStatement> statement = await runner.RunAsync(boardId, new BottleneckTask(), constraint, cancellationToken);
        if (statement.Value is BottleneckStatement checkedStatement)
            await StoreAsync(boardId, AiFeature.Bottleneck, [BottleneckTask.ToInsight(constraint, checkedStatement)], cancellationToken);
    }

    public async Task AdviseOnWipAsync(Guid boardId, CancellationToken cancellationToken)
    {
        if (await facts.FindSnapshotAsync(boardId, clock.GetUtcNow(), cancellationToken) is not { } snapshot) return;
        if (snapshot.Steps.All(step => step.WipLimit is null)) return;   // nothing to tune

        AiResult<WipAdvice> advice = await runner.RunAsync(boardId, new WipAdviceTask(), snapshot, cancellationToken);
        if (advice.Value is WipAdvice checkedAdvice)
            await StoreAsync(boardId, AiFeature.WipAdvice, WipAdviceTask.ToInsights(snapshot, checkedAdvice), cancellationToken);
    }

    private Task StoreAsync(Guid boardId, AiFeature kind, IReadOnlyList<Insight> found, CancellationToken cancellationToken) =>
        insights.ReplaceAsync(boardId, kind, found, clock.GetUtcNow(), cancellationToken);
}

// ---- forecast narrative (feature 5)

/// <summary>The book's forecast as numbers; the narrative may state these and nothing else.</summary>
public sealed record class ForecastFacts(
    int WindowDays, decimal FinishedPerDay, decimal AddedPerDay, int TasksRemaining, decimal? DaysRemaining, bool NeverFinishes)
{
    public static ForecastFacts Of(Forecast forecast, int windowDays) => new(
        windowDays, Rounded(forecast.CompletionPerDay), Rounded(forecast.AddPerDay), forecast.CurrentTaskEstimate,
        forecast.DaysRemaining is { } days ? Rounded(days) : null, forecast.NeverFinishes);

    public IEnumerable<decimal> Numbers => new decimal?[] { WindowDays, FinishedPerDay, AddedPerDay, TasksRemaining, DaysRemaining }.OfType<decimal>();

    /// <summary>To cents, halves away from zero: the same way the grounding check rounds what the model states.</summary>
    private static decimal Rounded(double value) => Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);
}

public sealed record class ForecastNarrative(string Summary);

public sealed class ForecastNarrativeTask : AiTask<ForecastFacts, ForecastNarrative>
{
    public const int MaxLength = 800;

    public override AiFeature Feature => AiFeature.ForecastNarrative;

    public override string Instructions =>
        "You explain a Kanban forecast to leadership in plain language, in four sentences at most: how fast tasks finish and"
        + " arrive per day over the window, how many remain, and when the work ends. When neverFinishes is true, say plainly"
        + " that work arrives at least as fast as it finishes, so no date can be given. State numbers exactly as given."
        + GroundRules;

    public override bool IsGrounded(ForecastFacts data, ForecastNarrative result) =>
        result.Summary is { Length: > 0 and <= MaxLength }
        && Grounding.MentionsOnly(result.Summary, [])
        && Grounding.NumbersComeFrom(result.Summary, data.Numbers);
}

/// <summary>Tells a board's forecast in words, on demand, for people with <c>ai.use</c> who may see its metrics.</summary>
public sealed class ForecastNarrator(IFlowFacts facts, AiRunner runner, IBoardAuthorizer authorizer)
{
    public const int WindowDays = 14;
    private const byte MustHave = 0;

    public async Task<Outcome<ForecastNarrative>> NarrateAsync(Guid boardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        Permission needed = Permission.AiUse | Permission.MetricsView;
        if (await authorizer.DenyUnlessAsync(user, boardId, needed, cancellationToken) is { } denied) return denied;

        ForecastInputs inputs = await facts.GetForecastInputsAsync(boardId, MustHave, WindowDays, cancellationToken);
        ForecastFacts forecast = ForecastFacts.Of(Forecast.From(inputs), WindowDays);
        AiResult<ForecastNarrative> narrative = await runner.RunAsync(boardId, new ForecastNarrativeTask(), forecast, cancellationToken);

        return narrative.Value is ForecastNarrative told ? told : new Invalid(((AiRefusal)narrative.Value!).Why);
    }
}
