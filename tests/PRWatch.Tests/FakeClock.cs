namespace PRWatch.Tests;

/// <summary>A clock that stays at a fixed time until a test moves it.</summary>
internal sealed class FakeClock : TimeProvider
{
    public static readonly DateTimeOffset DefaultNow = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    private readonly object gate = new();

    private DateTimeOffset now;

    public FakeClock(DateTimeOffset now)
    {
        this.now = now;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (gate)
        {
            now += by;
        }
    }
}
