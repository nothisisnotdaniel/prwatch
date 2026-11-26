namespace PRWatch;

/// <summary>The PR the watcher hands to the runner: its checked project and number, the head it saw, whether its
/// author is trusted and its pending request (<c>null</c> or <c>review</c>).</summary>
internal sealed record ReviewPick(string Project, int Number, string HeadSha, bool Trusted, string? Request);

/// <summary>Starts one review run at a time. The real <c>claude</c> runner comes with backlog item 4.</summary>
internal interface IReviewRunner
{
    /// <summary>True while a run is going; the watcher picks nothing then.</summary>
    public bool IsBusy { get; }

    /// <summary>Starts a run for <paramref name="pick"/>; false when nothing was started.</summary>
    public bool TryStart(ReviewPick pick);
}

/// <summary>The production runner until item 4: never busy, never starts anything.</summary>
internal sealed class NullReviewRunner : IReviewRunner
{
    public bool IsBusy => false;

    public bool TryStart(ReviewPick pick)
    {
        ArgumentNullException.ThrowIfNull(pick);
        return false;
    }
}
