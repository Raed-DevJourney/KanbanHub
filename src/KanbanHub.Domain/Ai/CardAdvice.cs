using System.Security.Claims;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.GitHub;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Ai;

/// <summary>A card as the model sees it. Every text member was written by users: it is data, never an instruction.</summary>
public sealed record class CardBrief(int Number, string Title, string? Description, string CardType)
{
    /// <summary>The done rule of the step the card is in.</summary>
    public IReadOnlyList<ChecklistLine> DoneRule { get; init; } = [];

    public static CardBrief Of(SyncedCard card, IReadOnlyList<ChecklistLine> doneRule) =>
        new(card.Number, card.Content.Title, card.Content.Description, card.Content.CardType.ToString()) { DoneRule = doneRule };
}

// ---- breakdown assistant (feature 6)

public sealed record class ProposedTask(string Title, IReadOnlyList<string> AcceptanceCriteria, decimal EstimatedDays);

/// <summary>Child tasks the AI proposes for a card. A person accepts, edits or ignores them.</summary>
public sealed record class BreakdownProposal(IReadOnlyList<ProposedTask> Tasks);

public sealed class BreakdownTask : AiTask<CardBrief, BreakdownProposal>
{
    public const int MaxTasks = 12;
    public const int MaxCriteria = 6;
    public const decimal MaxDays = 5;

    public override AiFeature Feature => AiFeature.Breakdown;

    public override string Instructions =>
        "You break a Kanban card down into child tasks. Propose 2 to 8 tasks that together finish the card."
        + " Each task must be small enough to finish in under a week: give estimatedDays between 0.5 and 5."
        + " Give each task 1 to 4 acceptance criteria that someone can check. Don't repeat the card itself as a task."
        + GroundRules;

    public override bool IsGrounded(CardBrief data, BreakdownProposal result) =>
        result.Tasks is { Count: > 0 and <= MaxTasks } && result.Tasks.All(task => IsSound(task, data));

    private static bool IsSound(ProposedTask task, CardBrief card) =>
        !string.IsNullOrWhiteSpace(task.Title) && task.Title.Length <= CardContent.MaxTitleLength
        && task.EstimatedDays is > 0 and <= MaxDays
        && task.AcceptanceCriteria is { Count: <= MaxCriteria }
        && task.AcceptanceCriteria.Append(task.Title).All(text => Grounding.MentionsOnly(text, [card.Number]));
}

// ---- triage assist (feature 9)

/// <summary>What the AI thinks a card is. The person applies it by editing the card, or doesn't.</summary>
public sealed record class TriageSuggestion(CardType CardType, byte PriorityBucket, string? Origin, string Why);

/// <summary>The card, and the values the model may choose from.</summary>
public sealed record class TriageBrief(CardBrief Card, IReadOnlyList<string> CardTypes, IReadOnlyList<string> Origins);

public sealed class TriageTask : AiTask<TriageBrief, TriageSuggestion>
{
    public const int MaxWhyLength = 400;

    public override AiFeature Feature => AiFeature.Triage;

    public override string Instructions =>
        "You triage a new Kanban card. Choose its cardType from cardTypes, a priorityBucket from 0 (must have) to 3 (nice idea),"
        + " and an origin code from origins, or null when none fits: U usage, X experiment, F feedback, S support,"
        + " L leadership, TF team feature, PF partner feature, B bug. Say why in one or two sentences."
        + GroundRules;

    public static TriageBrief BriefOf(CardBrief card) =>
        new(card, Enum.GetNames<CardType>(), [.. CardContent.OriginCodes.Order(StringComparer.Ordinal)]);

    public override bool IsGrounded(TriageBrief data, TriageSuggestion result) =>
        Enum.IsDefined(result.CardType)
        && result.PriorityBucket <= CardContent.LowestPriorityBucket
        && (result.Origin is null || CardContent.OriginCodes.Contains(result.Origin))
        && result.Why is { Length: > 0 and <= MaxWhyLength }
        && Grounding.MentionsOnly(result.Why, [data.Card.Number]);
}

// ---- done-rule checker (feature 7)

public sealed record class PullRequestFact(int Number, string State, bool IsDraft);

/// <summary>What the checker may reason from: the step's done rule as ticked so far, and the card's pull requests.</summary>
public sealed record class DoneRuleEvidence(CardBrief Card, IReadOnlyList<PullRequestFact> PullRequests);

public sealed record class UnmetItem(string Item, string Evidence);

/// <summary>Done-rule items that look unmet. Advisory: the checklist and the move stay with people.</summary>
public sealed record class DoneRuleReview(IReadOnlyList<UnmetItem> Unmet)
{
    public static DoneRuleReview NothingToCheck { get; } = new([]);
}

public sealed class DoneRuleCheckTask : AiTask<DoneRuleEvidence, DoneRuleReview>
{
    public const int MaxEvidenceLength = 400;

    public override AiFeature Feature => AiFeature.DoneRuleCheck;

    public override string Instructions =>
        "You check a Kanban card against the done rule of its step before it moves to Done. card.doneRule lists the items"
        + " and whether each is ticked. List the items that look unmet, quoting each item's text exactly, with the evidence:"
        + " an unticked required item, a pull request that is still a draft or not merged, or nothing in the data that shows it."
        + " List nothing when the rule looks met. You advise; you don't decide."
        + GroundRules;

    public override bool IsGrounded(DoneRuleEvidence data, DoneRuleReview result)
    {
        string[] items = [.. data.Card.DoneRule.Select(item => item.Text.Trim())];
        int[] citable = [data.Card.Number, .. data.PullRequests.Select(pullRequest => pullRequest.Number)];

        return result.Unmet.Count <= items.Length && result.Unmet.All(unmet =>
            Grounding.IsOneOf(unmet.Item, items)
            && unmet.Evidence is { Length: > 0 and <= MaxEvidenceLength }
            && Grounding.MentionsOnly(unmet.Evidence, citable));
    }
}

// ---- agent instructions (feature 10)

/// <summary>Instructions the AI drafts for a coding agent. The person edits them before sending the card (section 10.2).</summary>
public sealed record class InstructionDraft(string Instructions);

public sealed class AgentInstructionsTask : AiTask<CardBrief, InstructionDraft>
{
    public override AiFeature Feature => AiFeature.AgentInstructions;

    public override string Instructions =>
        "You draft instructions for a coding agent that will work on a Kanban card's GitHub issue. Write what the team would"
        + " tell a new colleague: the outcome wanted, what to leave alone, and how the done rule will be checked."
        + " Plain sentences, no more than 12 lines. Never include credentials, tokens or connection strings."
        + GroundRules;

    /// <summary>The draft passes the same guardrails a person's instructions do: its length, and no credentials.</summary>
    public override bool IsGrounded(CardBrief data, InstructionDraft result) =>
        !string.IsNullOrWhiteSpace(result.Instructions)
        && result.Instructions.Length <= AgentInstructions.MaxTeamInstructionsLength
        && SecretScanner.Find(result.Instructions).Count == 0
        && Grounding.MentionsOnly(result.Instructions, [data.Number]);
}

/// <summary>
/// AI help with one card, for people with <c>ai.use</c>. Everything it returns is a suggestion: applying it goes
/// through the same commands, permissions and rules as doing the thing by hand.
/// </summary>
public sealed class CardAssistant(ISyncedCards cards, IPullRequestStore pullRequests, AiRunner runner, IBoardAuthorizer authorizer)
{
    private static readonly NotFound NoSuchCard = new("card");

    public Task<Outcome<BreakdownProposal>> ProposeBreakdownAsync(Guid cardId, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        AskAsync(cardId, user, new BreakdownTask(), Task.FromResult, cancellationToken);

    public Task<Outcome<TriageSuggestion>> SuggestTriageAsync(Guid cardId, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        AskAsync(cardId, user, new TriageTask(), card => Task.FromResult(TriageTask.BriefOf(card)), cancellationToken);

    public Task<Outcome<InstructionDraft>> DraftAgentInstructionsAsync(Guid cardId, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        AskAsync(cardId, user, new AgentInstructionsTask(), Task.FromResult, cancellationToken);

    /// <summary>A step without a done rule has nothing to check, so the model isn't asked.</summary>
    public async Task<Outcome<DoneRuleReview>> ReviewDoneRuleAsync(Guid cardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        Outcome<SyncedCard> found = await FindReadableAsync(cardId, user, cancellationToken);
        if (found.Value is not SyncedCard) return Refused<DoneRuleReview>(found);
        if ((await cards.ListChecklistAsync(cardId, cancellationToken)).Count == 0) return DoneRuleReview.NothingToCheck;

        return await AskAsync(cardId, user, new DoneRuleCheckTask(), card => GatherEvidenceAsync(cardId, card, cancellationToken), cancellationToken);
    }

    private async Task<DoneRuleEvidence> GatherEvidenceAsync(Guid cardId, CardBrief card, CancellationToken cancellationToken)
    {
        IReadOnlyList<PullRequestLink> linked = await pullRequests.ListAsync(cardId, cancellationToken);
        return new DoneRuleEvidence(card, [.. linked.Select(link => new PullRequestFact(link.Number, link.State.ToString(), link.IsDraft))]);
    }

    private async Task<Outcome<TResult>> AskAsync<TData, TResult>(
        Guid cardId, ClaimsPrincipal user, AiTask<TData, TResult> task, Func<CardBrief, Task<TData>> gather, CancellationToken cancellationToken)
        where TData : notnull
        where TResult : class
    {
        Outcome<SyncedCard> found = await FindReadableAsync(cardId, user, cancellationToken);
        if (found.Value is not SyncedCard card) return Refused<TResult>(found);

        CardBrief brief = CardBrief.Of(card, await cards.ListChecklistAsync(cardId, cancellationToken));
        AiResult<TResult> answer = await runner.RunAsync(card.BoardId, task, await gather(brief), cancellationToken);

        return answer.Value is TResult result ? result : new Invalid(((AiRefusal)answer.Value!).Why);
    }

    private async Task<Outcome<SyncedCard>> FindReadableAsync(Guid cardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await cards.FindAsync(cardId, cancellationToken) is not { } card) return NoSuchCard;
        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.AiUse, cancellationToken) is { } denied) return denied;

        return card;
    }

    /// <summary>Carries a refusal over to the outcome of another type.</summary>
    private static Outcome<TResult> Refused<TResult>(Outcome<SyncedCard> refused)
        where TResult : class => refused.Value switch
        {
            Forbidden forbidden => forbidden,
            _ => NoSuchCard
        };
}
