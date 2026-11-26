namespace PRWatch;

/// <summary>The PR the watcher hands to the runner: its checked project and number, the head it saw, whether its
/// author is trusted and its pending request (<c>null</c> or one of <see cref="PrRules.Requests"/>, recorded as the run's <c>request</c>).</summary>
internal sealed record ReviewPick(string Project, int Number, string HeadSha, bool Trusted, string? Request);

/// <summary>Starts one review run at a time (<see cref="ClaudeReviewRunner"/> in production). A runner that
/// returns true has written the run's <c>headSha</c> and <c>startedAt</c> to the status.</summary>
internal interface IReviewRunner
{
    /// <summary>True while a run is going; the watcher picks nothing then.</summary>
    public bool IsBusy { get; }

    /// <summary>Starts a run for <paramref name="pick"/>; false when nothing was started.</summary>
    public bool TryStart(ReviewPick pick);
}
