using System.Collections.Concurrent;
using System.Globalization;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.GitHub;

namespace KanbanHub.AI;

/// <summary>
/// Stands in for the model on a development machine and in tests. It answers from the data it is handed, by
/// simple rules, so the features can be tried without Azure; a test can also script an answer, for example one
/// that invents a card, to see the grounding check stop it.
/// </summary>
public sealed class ScriptedAiModel : IAiModel
{
    private readonly ConcurrentDictionary<Type, Func<AiRequest, object?>> answers = new();
    private readonly ConcurrentQueue<AiRequest> requests = new();

    public ScriptedAiModel() => LocalAnswers.AddTo(this);

    /// <summary>Everything the model was asked, in order.</summary>
    public IReadOnlyList<AiRequest> Requests => [.. requests];

    public int TokensPerAnswer { get; set; } = 250;

    /// <summary>Scripts the answer for one kind of record; null plays an answer that wasn't valid JSON.</summary>
    public void Answer<TResult>(Func<AiRequest, TResult?> answer)
        where TResult : class => answers[typeof(TResult)] = answer;

    public Task<AiAnswer<TResult>> AskAsync<TResult>(AiRequest request, CancellationToken cancellationToken)
        where TResult : class
    {
        requests.Enqueue(request);
        var result = answers.TryGetValue(typeof(TResult), out Func<AiRequest, object?>? answer) ? (TResult?)answer(request) : null;
        return Task.FromResult(new AiAnswer<TResult>(result, TokensPerAnswer));
    }
}

/// <summary>What the stand-in says when nothing is scripted: plausible answers worked out from the data alone.</summary>
internal static class LocalAnswers
{
    private const int MaxListed = 3;

    public static void AddTo(ScriptedAiModel model)
    {
        model.Answer(request => BreakDown((CardBrief)request.Data));
        model.Answer(request => Triage((TriageBrief)request.Data));
        model.Answer(request => Review((DoneRuleEvidence)request.Data));
        model.Answer(request => Draft((CardBrief)request.Data));
        model.Answer(request => Brief((FlowSnapshot)request.Data));
        model.Answer(request => Explain((Constraint)request.Data));
        model.Answer(request => Advise((FlowSnapshot)request.Data));
        model.Answer(request => Narrate((ForecastFacts)request.Data));
        model.Answer(request => Explain((AgingCard)request.Data));
    }

    private static AgingExplanation Explain(AgingCard card) => new(Invariant(
        $"#{card.Number} has been in flow for {card.AgeDays} days; {AgingRule.Percentile}% of {card.CardType} cards finish within {card.ExpectedDays}. {(card.IsBlocked ? "It is blocked: can someone help clear that?" : "Is it too big, or waiting on someone?")}"));

    /// <summary>One task per line of the description when it lists some; otherwise design, build, verify.</summary>
    private static BreakdownProposal BreakDown(CardBrief card)
    {
        string[] lines = [.. (card.Description ?? "").Split('\n').Select(line => line.Trim(' ', '-', '*', '\r')).Where(line => line.Length > 0)];
        string[] titles = lines.Length >= 2 ? lines : [$"Design: {card.Title}", $"Build: {card.Title}", $"Verify: {card.Title}"];
        string[] criteria = card.DoneRule.Count > 0 ? [.. card.DoneRule.Take(MaxListed).Select(item => item.Text)] : ["A teammate reviewed it"];

        return new BreakdownProposal([.. titles.Take(BreakdownTask.MaxTasks).Select(title => new ProposedTask(Shorten(title), criteria, 2))]);
    }

    private static TriageSuggestion Triage(TriageBrief brief)
    {
        string text = $"{brief.Card.Title} {brief.Card.Description}";
        return Mentions(text, "bug", "error", "fail", "crash", "broken") ? new TriageSuggestion(CardType.Bug, 1, "B", "The text describes something that doesn't work.")
            : Mentions(text, "spike", "investigate", "explore") ? new TriageSuggestion(CardType.Spike, 2, "X", "The text asks a question to answer before building.")
            : new TriageSuggestion(CardType.Feature, 2, "TF", "The text describes something new to build.");
    }

    private static DoneRuleReview Review(DoneRuleEvidence evidence) => new(
    [
        .. evidence.Card.DoneRule
            .Where(item => item is { IsRequired: true, IsChecked: false })
            .Select(item => new UnmetItem(item.Text, OpenPullRequestNote(evidence) ?? "This required item isn't ticked yet."))
    ]);

    private static string? OpenPullRequestNote(DoneRuleEvidence evidence) =>
        evidence.PullRequests.FirstOrDefault(pullRequest => pullRequest.State != nameof(PullRequestState.Merged)) is { } open
            ? $"Not ticked, and pull request #{open.Number} isn't merged."
            : null;

    private static InstructionDraft Draft(CardBrief card)
    {
        string rule = card.DoneRule.Count == 0 ? "" : $"\nThe work is done when: {string.Join("; ", card.DoneRule.Select(item => item.Text))}.";
        return new InstructionDraft($"Work on card #{card.Number}. Keep the change small, follow the repository's conventions and add tests.{rule}");
    }

    private static StandupBrief Brief(FlowSnapshot board)
    {
        StepFlow busiest = board.Steps.MaxBy(step => step.ActiveCount)!;
        return new StandupBrief(
            [.. board.Finished.Select(card => card.Number)],
            [.. board.Blocked.OrderByDescending(card => card.Days).Select(card => card.Number)],
            [.. board.Aging.Take(MaxListed).Select(card => card.Number)],
            [new SwarmSuggestion(busiest.Name, Invariant($"{busiest.ActiveCount} cards are in progress there."))],
            Invariant($"{board.Finished.Count} finished since yesterday, {board.Blocked.Count} blocked, {board.Aging.Count} aging."));
    }

    private static BottleneckStatement Explain(Constraint found) => new(
        Invariant($"{found.ConstraintStep.Name} is the constraint: {found.WaitingStep.Name} Done holds {found.WaitingStep.DoneCount} cards, the oldest for {found.WaitingStep.OldestDoneDays} days."),
        $"Help {found.ConstraintStep.Name} finish what it has before starting more in {found.WaitingStep.Name}.");

    /// <summary>The book's last resort, and only with evidence: a limit people keep overriding is raised by one.</summary>
    private static WipAdvice Advise(FlowSnapshot board) => new(
    [
        .. board.Steps
            .Where(step => step is { WipLimit: not null, OverridesLast28Days: > 0 })
            .Take(WipAdviceTask.MaxProposals)
            .Select(step => new WipSuggestionLine(
                step.Name, step.WipLimit!.Value, step.WipLimit.Value + 1,
                $"The limit was overridden {Times(step.OverridesLast28Days)} in the last four weeks."))
    ]);

    private static string Times(int count) => count == 1 ? "once" : Invariant($"{count} times");

    private static ForecastNarrative Narrate(ForecastFacts forecast)
    {
        string pace = Invariant($"Over the last {forecast.WindowDays} days the team finished {forecast.FinishedPerDay} tasks a day while {forecast.AddedPerDay} arrived.");
        string end = forecast.DaysRemaining is { } days
            ? Invariant($"{forecast.TasksRemaining} tasks remain, about {days} days of work.")
            : Invariant($"{forecast.TasksRemaining} tasks remain, and work arrives at least as fast as it finishes, so no end date can be given.");
        return new ForecastNarrative($"{pace} {end}");
    }

    private static bool Mentions(string text, params ReadOnlySpan<string> words)
    {
        foreach (string word in words)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string Shorten(string title) =>
        title.Length <= CardContent.MaxTitleLength ? title : title[..CardContent.MaxTitleLength];

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
