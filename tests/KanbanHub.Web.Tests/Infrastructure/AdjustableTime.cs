namespace KanbanHub.Web.Tests.Infrastructure;

/// <summary>A clock a test moves by hand.</summary>
internal sealed class AdjustableTime(DateTime startUtc) : TimeProvider
{
    private DateTimeOffset now = new(startUtc, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}
