using KanbanHub.Domain.Ai;

namespace KanbanHub.Domain.Tests.Ai;

/// <summary>The gate in front of the model: environment, opt-in, budget, then the grounding check on the way back.</summary>
public class AiRunnerTests
{
    private static readonly Guid Board = Guid.NewGuid();
    private static readonly ForecastFacts Facts = new(14, 1.5m, 0.75m, 12, 16m, NeverFinishes: false);
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private readonly Policies policies = new();
    private readonly Model model = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<AiResult<ForecastNarrative>> RunAsync(bool configured = true) =>
        new AiRunner(new AiSettings(configured), model, policies, new FixedClock(Now)).RunAsync(Board, new ForecastNarrativeTask(), Facts, Ct);

    [Fact]
    public async Task A_grounded_answer_comes_through_and_its_cost_is_recorded()
    {
        AiResult<ForecastNarrative> result = await RunAsync();

        Assert.Equal(new ForecastNarrative("12 tasks remain."), result.Value);
        Assert.Equal((Board, AiFeature.ForecastNarrative, 300, Now), Assert.Single(policies.Recorded));
        Assert.Same(Facts, Assert.Single(model.Asked).Data);
    }

    [Fact]
    public async Task Without_a_model_in_the_environment_nothing_is_asked()
    {
        AiResult<ForecastNarrative> result = await RunAsync(configured: false);

        Assert.Same(AiRefusal.NotConfigured, result.Value);
        Assert.Empty(model.Asked);
    }

    [Fact]
    public async Task A_board_that_did_not_opt_in_or_does_not_exist_is_refused()
    {
        policies.Policy = new AiPolicy(IsEnabled: false, MonthlyTokenBudget: 1000, TokensUsedThisMonth: 0);
        AiResult<ForecastNarrative> switchedOff = await RunAsync();
        policies.Policy = null;
        AiResult<ForecastNarrative> unknown = await RunAsync();

        Assert.Same(AiRefusal.NotOptedIn, switchedOff.Value);
        Assert.Same(AiRefusal.NotOptedIn, unknown.Value);
        Assert.Empty(model.Asked);
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    [InlineData(5000, true)]
    public async Task The_budget_is_a_soft_stop_at_the_months_usage(long used, bool refused)
    {
        policies.Policy = new AiPolicy(IsEnabled: true, MonthlyTokenBudget: 1000, TokensUsedThisMonth: used);

        AiResult<ForecastNarrative> result = await RunAsync();

        Assert.Equal(refused, ReferenceEquals(result.Value, AiRefusal.OverBudget));
        Assert.Equal(new DateOnly(2026, 11, 16), policies.AskedForMonth);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("99 tasks remain.")]
    public async Task An_answer_that_is_missing_or_fails_the_grounding_check_is_dropped_but_its_cost_still_counts(string? summary)
    {
        model.Summary = summary;

        AiResult<ForecastNarrative> result = await RunAsync();

        Assert.Same(AiRefusal.NothingUsable, result.Value);
        Assert.Single(policies.Recorded);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Model : IAiModel
    {
        public string? Summary { get; set; } = "12 tasks remain.";

        public List<AiRequest> Asked { get; } = [];

        public Task<AiAnswer<TResult>> AskAsync<TResult>(AiRequest request, CancellationToken cancellationToken)
            where TResult : class
        {
            Asked.Add(request);
            object? answer = Summary is null ? null : new ForecastNarrative(Summary);
            return Task.FromResult(new AiAnswer<TResult>((TResult?)answer, 300));
        }
    }

    private sealed class Policies : IAiPolicyStore
    {
        public AiPolicy? Policy { get; set; } = new(IsEnabled: true, MonthlyTokenBudget: 1000, TokensUsedThisMonth: 0);

        public DateOnly AskedForMonth { get; private set; }

        public List<(Guid Board, AiFeature Feature, int Tokens, DateTimeOffset At)> Recorded { get; } = [];

        public Task<AiPolicy?> FindAsync(Guid boardId, DateOnly month, CancellationToken cancellationToken)
        {
            AskedForMonth = month;
            return Task.FromResult(Policy);
        }

        public Task RecordUsageAsync(Guid boardId, AiFeature feature, int tokens, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Recorded.Add((boardId, feature, tokens, now));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AiBoard>> ListEnabledBoardsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiBoard>>([]);
    }
}

public class BriefScheduleTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);
    private static readonly AiBoard Amsterdam = new(Guid.NewGuid(), new TimeOnly(9, 30), "Europe/Amsterdam");

    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 11, 16, hour, minute, 0, TimeSpan.Zero);

    /// <summary>Standup at 09:30 in Amsterdam (UTC+1 in November): the brief is due at 09:00 local, 08:00 UTC.</summary>
    [Theory]
    [InlineData(7, 59, false)]
    [InlineData(8, 0, true)]
    [InlineData(8, 4, true)]
    [InlineData(8, 5, false)]
    [InlineData(9, 0, false)]
    public void The_brief_is_due_in_the_one_tick_that_starts_half_an_hour_before_standup(int hour, int minute, bool due)
    {
        Assert.Equal(due, BriefSchedule.IsBriefDue(Amsterdam, Utc(hour, minute), Tick));
    }

    [Fact]
    public void A_standup_just_after_midnight_gets_its_brief_the_evening_before()
    {
        var nightOwls = new AiBoard(Guid.NewGuid(), new TimeOnly(0, 10), "UTC");

        Assert.True(BriefSchedule.IsBriefDue(nightOwls, Utc(23, 41), Tick));
        Assert.False(BriefSchedule.IsBriefDue(nightOwls, Utc(23, 35), Tick));
    }

    [Fact]
    public void A_board_without_a_standup_time_or_with_an_unknown_time_zone_is_never_due()
    {
        Assert.False(BriefSchedule.IsBriefDue(Amsterdam with { StandupTimeLocal = null }, Utc(8, 0), Tick));
        Assert.False(BriefSchedule.IsBriefDue(Amsterdam with { TimeZoneId = "Mars/Olympus_Mons" }, Utc(8, 0), Tick));
    }

    [Fact]
    public async Task The_scheduler_queues_a_brief_for_each_board_that_is_due_and_every_board_for_the_hourly_check()
    {
        var late = new AiBoard(Guid.NewGuid(), new TimeOnly(15, 0), "UTC");
        var queue = new Queue();
        var scheduler = new AiScheduler(new Boards(Amsterdam, late), queue, new FixedClock(Utc(8, 2)));

        await scheduler.QueueDueStandupBriefsAsync(TestContext.Current.CancellationToken);
        await scheduler.QueueForEveryBoardAsync(AiFeature.Bottleneck, TestContext.Current.CancellationToken);

        Assert.Equal(
            [new AiJob(Amsterdam.BoardId, AiFeature.StandupBrief), new AiJob(Amsterdam.BoardId, AiFeature.Bottleneck), new AiJob(late.BoardId, AiFeature.Bottleneck)],
            queue.Jobs);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Queue : IAiJobInbox
    {
        public List<AiJob> Jobs { get; } = [];

        public Task EnqueueAsync(AiJob job, CancellationToken cancellationToken)
        {
            Jobs.Add(job);
            return Task.CompletedTask;
        }
    }

    private sealed class Boards(params AiBoard[] enabled) : IAiPolicyStore
    {
        public Task<AiPolicy?> FindAsync(Guid boardId, DateOnly month, CancellationToken cancellationToken) => Task.FromResult<AiPolicy?>(null);

        public Task RecordUsageAsync(Guid boardId, AiFeature feature, int tokens, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<AiBoard>> ListEnabledBoardsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AiBoard>>(enabled);
    }
}
