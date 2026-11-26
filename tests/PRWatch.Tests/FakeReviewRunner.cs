namespace PRWatch.Tests;

/// <summary>A review runner for tests: <see cref="IsBusy"/> and the <see cref="TryStart"/> result are set by the
/// test, and every call is recorded. It never starts a process.</summary>
internal sealed class FakeReviewRunner : IReviewRunner
{
    private readonly object gate = new();

    private readonly List<ReviewPick> calls = [];

    public bool IsBusy { get; set; }

    public bool Result { get; set; } = true;

    public IReadOnlyList<ReviewPick> Calls
    {
        get
        {
            lock (gate)
            {
                return [.. calls];
            }
        }
    }

    public bool TryStart(ReviewPick pick)
    {
        lock (gate)
        {
            calls.Add(pick);
        }

        return Result;
    }
}
