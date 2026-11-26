namespace PRWatch.Tests;

/// <summary>A review runner for tests: <see cref="IsBusy"/> and the <see cref="TryStart"/> result are set by the
/// test, and every call is recorded. Like the real runner, a true result writes the run's <c>headSha</c> and
/// <c>startedAt</c> to <see cref="Status"/> (when set) at <see cref="Clock"/>'s time. It never starts a
/// process.</summary>
internal sealed class FakeReviewRunner : IReviewRunner
{
    private readonly object gate = new();

    private readonly List<ReviewPick> calls = [];

    public bool IsBusy { get; set; }

    public bool Result { get; set; } = true;

    public StatusStore? Status { get; set; }

    public TimeProvider Clock { get; set; } = TimeProvider.System;

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

        if (Result && Status is not null)
        {
            DateTimeOffset now = Clock.GetUtcNow();
            Status.Update(previous => previous with
            {
                Runs = previous.Runs.SetItem(
                    PrRules.RunKey(pick.Project, pick.Number), new RunStatus { HeadSha = pick.HeadSha, StartedAt = now }),
            });
        }

        return Result;
    }
}
