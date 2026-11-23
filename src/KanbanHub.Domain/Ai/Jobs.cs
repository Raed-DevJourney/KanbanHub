namespace KanbanHub.Domain.Ai;

/// <summary>A piece of background analysis for one board (section 11: AI runs in background workers).</summary>
public sealed record class AiJob(Guid BoardId, AiFeature Feature);

/// <summary>Where jobs wait: the <c>ai-jobs</c> queue in Azure, nowhere on a development machine.</summary>
public interface IAiJobInbox
{
    Task EnqueueAsync(AiJob job, CancellationToken cancellationToken);
}

/// <summary>Runs a job by handing it to the analysis it names. A feature that isn't background work is ignored.</summary>
public sealed class AiJobRunner(FlowAnalyst analyst)
{
    public Task RunAsync(AiJob job, CancellationToken cancellationToken) => job.Feature switch
    {
        AiFeature.StandupBrief => analyst.BriefStandupAsync(job.BoardId, cancellationToken),
        AiFeature.Bottleneck => analyst.FindBottleneckAsync(job.BoardId, cancellationToken),
        AiFeature.WipAdvice => analyst.AdviseOnWipAsync(job.BoardId, cancellationToken),
        _ => Task.CompletedTask
    };
}

/// <summary>Stands in for the queue where there is no Service Bus: the job runs at once, in this process.</summary>
public sealed class InlineAiJobInbox(AiJobRunner runner) : IAiJobInbox
{
    public Task EnqueueAsync(AiJob job, CancellationToken cancellationToken) => runner.RunAsync(job, cancellationToken);
}

/// <summary>When a board's standup brief is due: half an hour before its standup, in the board's own time zone.</summary>
public static class BriefSchedule
{
    public static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// True when the brief's moment fell within the last <paramref name="tick"/>. A timer that fires every tick
    /// therefore asks for each board's brief exactly once a day, without remembering anything.
    /// </summary>
    public static bool IsBriefDue(AiBoard board, DateTimeOffset now, TimeSpan tick)
    {
        if (board.StandupTimeLocal is not { } standup) return false;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(board.TimeZoneId, out TimeZoneInfo? zone)) return false;

        TimeSpan localTime = TimeZoneInfo.ConvertTime(now, zone).TimeOfDay;
        TimeSpan sinceBriefTime = Wrap(localTime - (standup.ToTimeSpan() - LeadTime));
        return sinceBriefTime < tick;
    }

    /// <summary>Keeps a difference of two times of day within one day, so a standup just after midnight works too.</summary>
    private static TimeSpan Wrap(TimeSpan difference) =>
        TimeSpan.FromTicks(((difference.Ticks % TimeSpan.TicksPerDay) + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay);
}

/// <summary>Decides which boards get which background analysis now. Only boards that opted in are ever queued.</summary>
public sealed class AiScheduler(IAiPolicyStore policies, IAiJobInbox queue, TimeProvider clock)
{
    /// <summary>How often <see cref="QueueDueStandupBriefsAsync"/> is expected to be called.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    public async Task QueueDueStandupBriefsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        foreach (AiBoard board in await policies.ListEnabledBoardsAsync(cancellationToken))
        {
            if (BriefSchedule.IsBriefDue(board, now, Tick))
                await queue.EnqueueAsync(new AiJob(board.BoardId, AiFeature.StandupBrief), cancellationToken);
        }
    }

    public async Task QueueForEveryBoardAsync(AiFeature feature, CancellationToken cancellationToken)
    {
        foreach (AiBoard board in await policies.ListEnabledBoardsAsync(cancellationToken))
            await queue.EnqueueAsync(new AiJob(board.BoardId, feature), cancellationToken);
    }
}
