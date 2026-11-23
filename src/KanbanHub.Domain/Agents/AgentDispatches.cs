using System.Security.Claims;
using System.Text.RegularExpressions;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.GitHub;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Agents;

/// <summary>Everything a dispatch decision is made from: the card, its issue and repository, the board's agent slots.</summary>
public sealed record class DispatchContext(SyncedCard Card, IssueLink? Link, RepoLink? Repository, AgentCapacity Capacity, AgentSession? Latest)
{
    /// <summary>The session an agent still has open on the card, if any.</summary>
    public AgentSession? Open => Latest is { } session && session.State.IsOpen() ? session : null;
}

/// <summary>Loads a <see cref="DispatchContext"/> in one place, so the rules themselves need no storage.</summary>
public sealed class DispatchContexts(
    ISyncedCards cards, IIssueLinkStore links, IRepoLinkStore repositories, IAgentSessionStore sessions)
{
    public async Task<DispatchContext?> LoadAsync(Guid cardId, CancellationToken cancellationToken)
    {
        if (await cards.FindAsync(cardId, cancellationToken) is not { } card) return null;

        IssueLink? link = await links.FindByCardAsync(cardId, cancellationToken);
        RepoLink? repository = link is null ? null : await repositories.FindAsync(link.Issue.RepositoryId, cancellationToken);
        AgentCapacity capacity = await sessions.FindCapacityAsync(card.BoardId, cancellationToken);
        return new DispatchContext(card, link, repository, capacity, await sessions.FindLatestAsync(cardId, cancellationToken));
    }

    /// <summary>The done rule the agent is told about: the items of the step the card is in.</summary>
    public Task<IReadOnlyList<ChecklistLine>> ListDoneRuleAsync(Guid cardId, CancellationToken cancellationToken) =>
        cards.ListChecklistAsync(cardId, cancellationToken);
}

/// <summary>The card already sits where an agent may work on it.</summary>
public sealed record class InPlace;

/// <summary>The card waits in the Done of the step before: dispatching pulls it in, through the normal WIP check.</summary>
public sealed record class NeedsPull(MoveTarget Target);

public sealed record class NotEligible(string Why);

/// <summary>Where a card stands relative to the steps that are open to agents (section 10.2).</summary>
public union Placement(InPlace, NeedsPull, NotEligible);

/// <summary>The rules a dispatch must pass before anything is sent anywhere. Pure: no storage, no clock.</summary>
public static partial class DispatchRules
{
    public const int MaxBranchLength = 200;
    public const string CardBusy = "An agent is already working on this card.";

    public static Placement PlacementOf(SyncedCard card)
    {
        SyncedStep[] open = [.. card.Steps.Where(step => step.IsAgentEligible)];
        if (open.Length == 0) return new NotEligible("No step of this board is open to agents. An owner can mark one under Settings, Workflow.");
        if (card.Step is { IsAgentEligible: true } here) return InStep(card, here);

        SyncedStep? next = open.FirstOrDefault(step => step.Order == card.Step?.Order + 1);
        return card.Location is Location.Done && MoveTargets.Active(next) is { } target
            ? new NeedsPull(target)
            : new NotEligible($"Agents work in {string.Join(" or ", open.Select(step => step.Name))}. Move the card there, or to Done of the step before.");
    }

    /// <summary>What about the card itself stands in the way, or null when nothing does.</summary>
    public static string? FindObstacle(DispatchContext context) =>
        context.Link is null || context.Repository is null ? "Link the card to a GitHub issue first."
        : context.Open is not null ? CardBusy
        : context.Capacity.IsFull ? AtLimit(context.Capacity)
        : PlacementOf(context.Card).Value is NotEligible notEligible ? notEligible.Why
        : null;

    /// <summary>What is wrong with what the person asked for, or null when it can be sent.</summary>
    public static string? FindProblem(DispatchCommand command, DispatchContext context) =>
        AgentCatalog.Find(command.Agent) is null ? "Unknown agent."
        : !BranchName().IsMatch(command.BaseBranch) ? $"Name the base branch (up to {MaxBranchLength} characters), for example main."
        : command.Instructions?.Length > AgentInstructions.MaxTeamInstructionsLength
            ? $"The instructions can be at most {AgentInstructions.MaxTeamInstructionsLength} characters."
        : SecretScanner.Find(AgentInstructions.Compose(command.Instructions, context.Card, [])) is { Count: > 0 } found
            ? $"The instructions or the card seem to contain {string.Join(", ", found)}. Remove it before sending the card to an agent."
        : null;

    public static string AtLimit(AgentCapacity capacity) =>
        $"This board has {capacity.Open} agent sessions open and allows {capacity.Limit}. Review one of them first.";

    private static Placement InStep(SyncedCard card, SyncedStep step) => card.Location switch
    {
        Location.Active => new InPlace(),
        Location.Track => new NotEligible("The card is set aside in Blocked. Bring it back to Active first."),
        _ => new NotEligible($"The card is already done in {step.Name}.")
    };

    [GeneratedRegex(@"^[A-Za-z0-9._/\-]{1,200}$")]
    private static partial Regex BranchName();
}

/// <summary>The line to GitHub's agents for one person: their own token first, then the adapter.</summary>
public sealed class AgentLine(GitHubConnection connection, IAgentDispatcher dispatcher)
{
    public const string ConnectFirst = "Connect your GitHub account first: agents are dispatched in your name and on your Copilot plan.";

    public Task<GitHubAccount?> FindAccountAsync(ClaimsPrincipal user, CancellationToken cancellationToken) =>
        connection.FindAsync(user, cancellationToken);

    /// <summary>Returns null when the agent was started, or why it wasn't.</summary>
    public async Task<string?> SendAsync(Guid userId, AgentAssignment assignment, CancellationToken cancellationToken)
    {
        if (await connection.GetFreshTokenAsync(userId, cancellationToken) is not { } token) return ConnectFirst;

        try
        {
            await dispatcher.DispatchAsync(token, assignment, cancellationToken);
            return null;
        }
        catch (AgentDispatchException refused)
        {
            return refused.Message;
        }
    }

    /// <summary>The agents GitHub says can be assigned in the repository, so people only see ones that will work.</summary>
    public async Task<IReadOnlyList<Agent>> ListAvailableAsync(Guid userId, string repository, CancellationToken cancellationToken)
    {
        if (await connection.GetFreshTokenAsync(userId, cancellationToken) is not { } token) return [];

        IReadOnlyList<string> assignable = await dispatcher.ListAssignableAsync(token, repository, cancellationToken);
        return [.. AgentCatalog.All.Where(agent => assignable.Any(agent.Answers))];
    }
}

/// <summary>What the "Send to agent" dialog shows: who is connected, which agents are on offer, and what stands in the way.</summary>
public sealed record class AgentDesk(bool IsEnabled, AgentCapacity Capacity)
{
    public GitHubAccount? Account { get; init; }

    public IReadOnlyList<Agent> Agents { get; init; } = [];

    public string? Repository { get; init; }

    public AgentSession? Latest { get; init; }

    /// <summary>Why a dispatch can't be sent right now; null when it can.</summary>
    public string? WhyNot { get; init; }
}

/// <summary>
/// Sends cards to coding agents (section 10): checks the rules, pulls the card into place through the normal
/// WIP check, takes one of the board's agent slots, and only then asks GitHub. A refusal frees the slot again.
/// </summary>
public sealed class AgentDispatches(
    AgentSettings settings, DispatchContexts contexts, AgentLine line, IAgentSessionStore sessions,
    MoveCardHandler mover, IBoardAuthorizer authorizer, TimeProvider clock)
{
    public const string SwitchedOff = "Agent dispatch is switched off here. Assign the issue to the agent on GitHub instead.";
    public const string NoAgentOffered = "GitHub offers no agent for this repository. Check that Copilot cloud agent is enabled for it and for your account.";

    private static readonly NotFound NoSuchCard = new("card");

    public async Task<Outcome<AgentDesk>> DescribeAsync(Guid cardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await contexts.LoadAsync(cardId, cancellationToken) is not { } context) return NoSuchCard;
        if (await authorizer.DenyUnlessAsync(user, context.Card.BoardId, Permission.BoardRead, cancellationToken) is { } denied) return denied;

        GitHubAccount? account = await line.FindAccountAsync(user, cancellationToken);
        IReadOnlyList<Agent> agents = settings.IsEnabled && account is not null && context.Repository is { } repository
            ? await line.ListAvailableAsync(account.UserId, repository.FullName, cancellationToken)
            : [];

        return new AgentDesk(settings.IsEnabled, context.Capacity)
        {
            Account = account,
            Agents = agents,
            Repository = context.Repository?.FullName,
            Latest = context.Latest,
            WhyNot = WhyNot(context, account, agents)
        };
    }

    public async Task<Outcome<AgentSession>> DispatchAsync(DispatchCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!settings.IsEnabled) return new Invalid(SwitchedOff);
        if (await contexts.LoadAsync(command.CardId, cancellationToken) is not { } context) return NoSuchCard;
        if (await authorizer.DenyUnlessAsync(user, context.Card.BoardId, Permission.AgentDispatch, cancellationToken) is { } denied) return denied;
        if ((DispatchRules.FindObstacle(context) ?? DispatchRules.FindProblem(command, context)) is { } problem) return new Invalid(problem);
        if (await line.FindAccountAsync(user, cancellationToken) is null) return new Invalid(AgentLine.ConnectFirst);
        if (await PullIntoPlaceAsync(context, user, cancellationToken) is { } notPulled) return new Invalid(notPulled);

        return await StartAsync(context, command, user.GetObjectId(), cancellationToken);
    }

    /// <summary>
    /// Stops tracking the card's session and frees its slot. The agent itself is stopped on GitHub, by unassigning it.
    /// </summary>
    public async Task<Outcome<Saved>> EndAsync(Guid cardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await contexts.LoadAsync(cardId, cancellationToken) is not { } context) return NoSuchCard;
        if (await authorizer.DenyUnlessAsync(user, context.Card.BoardId, Permission.AgentDispatch, cancellationToken) is { } denied) return denied;
        if (context.Open is not { } open) return new NotFound("agent session");

        await sessions.UpdateAsync(open.Id, new SessionProgress(DispatchState.Ended), clock.GetUtcNow(), cancellationToken);
        return Saved.Instance;
    }

    private string? WhyNot(DispatchContext context, GitHubAccount? account, IReadOnlyList<Agent> agents) =>
        !settings.IsEnabled ? SwitchedOff
        : DispatchRules.FindObstacle(context) is { } obstacle ? obstacle
        : account is null ? AgentLine.ConnectFirst
        : agents.Count == 0 ? NoAgentOffered
        : null;

    /// <summary>"WIP first": a card waiting in the step before is pulled in as the person, so every gate applies.</summary>
    private async Task<string?> PullIntoPlaceAsync(DispatchContext context, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (DispatchRules.PlacementOf(context.Card).Value is not NeedsPull pull) return null;

        var move = new MoveCardCommand
        {
            CardId = context.Card.CardId,
            ExpectedRowVersion = context.Card.RowVersion,
            ToStepId = pull.Target.StepId,
            ToLocation = pull.Target.Location,
            IdempotencyKey = Guid.NewGuid()
        };
        MoveOutcome outcome = await mover.HandleAsync(move, user, cancellationToken);
        return outcome.Value switch
        {
            Moved => null,
            Forbidden forbidden => $"Sending also pulls the card into {pull.Target.Name}, and you lack the {forbidden.Missing} permission for that.",
            _ => MoveReplies.Describe(outcome, pull.Target)
        };
    }

    private async Task<Outcome<AgentSession>> StartAsync(
        DispatchContext context, DispatchCommand command, Guid userId, CancellationToken cancellationToken)
    {
        Agent agent = AgentCatalog.Find(command.Agent)!;
        string repository = context.Repository!.FullName;
        IReadOnlyList<ChecklistLine> doneRule = await contexts.ListDoneRuleAsync(command.CardId, cancellationToken);
        string instructions = AgentInstructions.Compose(command.Instructions, context.Card, doneRule);
        var stamp = new Stamp(userId, clock.GetUtcNow());

        AgentSession session = SessionOf(command, agent, repository, stamp);
        StartResult taken = await sessions.StartAsync(session, AgentInstructions.Hash(instructions), stamp, cancellationToken);
        if (taken is not StartResult.Started)
            return new Invalid(taken is StartResult.BoardAtLimit ? DispatchRules.AtLimit(context.Capacity) : DispatchRules.CardBusy);

        var assignment = new AgentAssignment(repository, context.Link!.Issue.Number, agent.AssigneeLogin)
        {
            TargetRepository = repository,
            BaseBranch = command.BaseBranch,
            Instructions = instructions,
            CustomAgent = command.CustomAgent,
            Model = session.Model
        };
        return await SendAsync(session, assignment, cancellationToken) is { } refused ? new Invalid(refused) : session;
    }

    /// <summary>Asks GitHub to start the agent. Whatever goes wrong, the slot taken a moment ago is given back.</summary>
    private async Task<string?> SendAsync(AgentSession session, AgentAssignment assignment, CancellationToken cancellationToken)
    {
        string? refused;
        try
        {
            refused = await line.SendAsync(session.DispatchedBy, assignment, cancellationToken);
        }
        catch
        {
            await FailAsync(session);
            throw;
        }

        if (refused is not null) await FailAsync(session);
        return refused;
    }

    /// <summary>Not cancellable: a request that was given up on must not leave its slot taken.</summary>
    private Task FailAsync(AgentSession session) =>
        sessions.UpdateAsync(session.Id, new SessionProgress(DispatchState.Failed), clock.GetUtcNow(), CancellationToken.None);

    private static AgentSession SessionOf(DispatchCommand command, Agent agent, string repository, Stamp stamp) => new()
    {
        Id = Guid.NewGuid(),
        CardId = command.CardId,
        Agent = agent.Key,
        TargetRepository = repository,
        BaseBranch = command.BaseBranch,
        Model = string.IsNullOrWhiteSpace(command.Model) ? null : command.Model.Trim(),
        DispatchedBy = stamp.ActorId,
        DispatchedAt = stamp.Now
    };
}

/// <summary>
/// Follows a session after it was sent (section 10.2, "Track"): the pull request the agent opens moves it to
/// review, merging or closing that pull request ends it, and so does unassigning the agent on GitHub.
/// </summary>
public sealed class AgentTracking(IAgentSessionStore sessions, IIssueLinkStore links, TimeProvider clock)
{
    public async Task FollowAsync(PullRequestLink pullRequest, string authorLogin, CancellationToken cancellationToken)
    {
        if (await sessions.FindOpenAsync(pullRequest.CardId, cancellationToken) is not { } session) return;
        if (!IsTheAgents(session, pullRequest, authorLogin)) return;

        DispatchState state = pullRequest.State switch
        {
            PullRequestState.Merged => DispatchState.Merged,
            PullRequestState.Closed => DispatchState.Closed,
            _ => DispatchState.InReview
        };
        if (session.State == state && session.PullRequestNumber == pullRequest.Number) return;

        await sessions.UpdateAsync(session.Id, new SessionProgress(state, pullRequest.Number), clock.GetUtcNow(), cancellationToken);
    }

    /// <summary>Returns false when the unassignment concerns no session.</summary>
    public async Task<bool> UnassignedAsync(AgentUnassigned change, CancellationToken cancellationToken)
    {
        if (await links.FindByIssueAsync(change.Issue.RepositoryId, change.Issue.Number, cancellationToken) is not { } link) return false;
        if (await sessions.FindOpenAsync(link.CardId, cancellationToken) is not { State: DispatchState.Working } session) return false;
        if (AgentCatalog.Find(session.Agent)?.Answers(change.AssigneeLogin) != true) return false;

        await sessions.UpdateAsync(session.Id, new SessionProgress(DispatchState.Ended), clock.GetUtcNow(), cancellationToken);
        return true;
    }

    /// <summary>The session's own pull request once it has one; before that, one the agent itself opened.</summary>
    private static bool IsTheAgents(AgentSession session, PullRequestLink pullRequest, string authorLogin) =>
        session.PullRequestNumber is { } known
            ? known == pullRequest.Number
            : AgentCatalog.Find(session.Agent)?.Answers(authorLogin) == true;
}
