namespace PRWatch.Tests;

/// <summary>The poll timeout against a clean exit (story 015): the timeout runs on a <see cref="FakeClock"/>, and
/// the runner's <see cref="PollRunner.AfterExit"/> hook fires it only after the fake <c>job.py</c> has exited and
/// both pipes are settled. Each test has its own rig (temp folder, factory root, fake <c>job.py</c>, clock).</summary>
public sealed class PollRunnerTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    /// <summary>Moves the clock past the timeout and waits until its token is cancelled.</summary>
    private static async Task FireTimeout(WatcherRig rig, CancellationToken timeout)
    {
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await WatcherRig.WaitFor(() => Task.FromResult(timeout.IsCancellationRequested), "the poll timeout to fire");
    }

    [Fact]
    public async Task CleanExit_ThenTimeout_CountsAsASuccessfulPoll()
    {
        using WatcherRig rig = new(timeout: OneSecond, pollOnClock: true, afterExit: FireTimeout);
        rig.Output([WatcherRig.Pr("Alpha", 1)]);

        await rig.Poll();

        Assert.Null(rig.Status.Current.LastError);
        Assert.NotNull(rig.Status.Current.LastPoll);
        PrStatus pr = Assert.Single(rig.Status.Current.Prs);
        Assert.Equal("Alpha", pr.Project);
        Assert.Equal(1, pr.Number);
    }

    [Fact]
    public async Task NonZeroExit_ThenTimeout_GivesPollFailed()
    {
        using WatcherRig rig = new(timeout: OneSecond, pollOnClock: true, afterExit: FireTimeout);
        rig.Output([WatcherRig.Pr("Alpha", 1)]);
        FakeJobPy.SetExit(rig.Root, 1);

        await rig.Poll();

        Assert.Equal(StatusError.PollFailed, rig.Status.Current.LastError?.Code);
        Assert.Null(rig.Status.Current.LastPoll);
    }

    [Fact]
    public async Task TimeoutWhileRunning_StillGivesPollTimeout_AndKillsTheTree()
    {
        using WatcherRig rig = new(timeout: OneSecond, pollOnClock: true);
        FakeJobPy.SetSleep(rig.Root, 60);

        Task poll = rig.Poll();
        await WatcherRig.WaitFor(() => Task.FromResult(rig.Calls().Count == 1), "the poll to start");
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await poll.WaitAsync(WatcherRig.Bound);

        Assert.Equal(StatusError.PollTimeout, rig.Status.Current.LastError?.Code);
        Assert.Null(rig.Status.Current.LastPoll);
        int pid = Assert.Single(rig.Calls()).GetProperty("pid").GetInt32();
        await WatcherRig.WaitFor(() => Task.FromResult(!WatcherRig.IsRunning(pid)), "the poll to exit");
    }

    [Fact]
    public async Task CallerCancel_WhileRunning_KillsThePoll_AndThrows()
    {
        using WatcherRig rig = new(pollOnClock: true);
        FakeJobPy.SetSleep(rig.Root, 60);
        using CancellationTokenSource caller = new();

        Task<PollOutcome> run = rig.Poller().RunAsync(rig.Root, caller.Token);
        await WatcherRig.WaitFor(() => Task.FromResult(rig.Calls().Count == 1), "the poll to start");
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(WatcherRig.Bound));
        int pid = Assert.Single(rig.Calls()).GetProperty("pid").GetInt32();
        await WatcherRig.WaitFor(() => Task.FromResult(!WatcherRig.IsRunning(pid)), "the poll to exit");
    }

    [Fact]
    public async Task CallerCancel_AfterACleanExit_Throws()
    {
        using CancellationTokenSource caller = new();
        using WatcherRig rig = new(pollOnClock: true, afterExit: (_, _) => caller.CancelAsync());
        rig.Output([WatcherRig.Pr("Alpha", 1)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Poller().RunAsync(rig.Root, caller.Token).WaitAsync(WatcherRig.Bound));
        Assert.Single(rig.Calls());
    }

    /// <summary>A kill counter for <see cref="PollRunner.BeforeKill"/> (story 040).</summary>
    private sealed class Kills
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public void Add()
        {
            Interlocked.Increment(ref count);
        }
    }

    [Fact]
    public async Task CleanExit_ThenTimeout_NeverKills()
    {
        Kills kills = new();
        using WatcherRig rig = new(timeout: OneSecond, pollOnClock: true, afterExit: FireTimeout, beforeKill: kills.Add);
        rig.Output([WatcherRig.Pr("Alpha", 1)]);

        await rig.Poll();

        Assert.Equal(0, kills.Count);
        Assert.Null(rig.Status.Current.LastError);
        Assert.NotNull(rig.Status.Current.LastPoll);
        PrStatus pr = Assert.Single(rig.Status.Current.Prs);
        Assert.Equal("Alpha", pr.Project);
        Assert.Equal(1, pr.Number);
    }

    [Fact]
    public async Task CleanExit_ThenCallerCancel_NeverKills_AndThrows()
    {
        Kills kills = new();
        using CancellationTokenSource caller = new();
        using WatcherRig rig = new(pollOnClock: true, afterExit: (_, _) => caller.CancelAsync(), beforeKill: kills.Add);
        rig.Output([WatcherRig.Pr("Alpha", 1)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Poller().RunAsync(rig.Root, caller.Token).WaitAsync(WatcherRig.Bound));
        Assert.Equal(0, kills.Count);
        Assert.Single(rig.Calls());
    }

    [Fact]
    public async Task NonZeroExit_ThenTimeout_NeverKills()
    {
        Kills kills = new();
        using WatcherRig rig = new(timeout: OneSecond, pollOnClock: true, afterExit: FireTimeout, beforeKill: kills.Add);
        rig.Output([WatcherRig.Pr("Alpha", 1)]);
        FakeJobPy.SetExit(rig.Root, 1);

        await rig.Poll();

        Assert.Equal(0, kills.Count);
        Assert.Equal(StatusError.PollFailed, rig.Status.Current.LastError?.Code);
        Assert.Null(rig.Status.Current.LastPoll);
    }

    [Fact]
    public async Task TimeoutWhileRunning_Kills()
    {
        Kills kills = new();
        using WatcherRig rig = new(timeout: OneSecond, pollOnClock: true, beforeKill: kills.Add);
        FakeJobPy.SetSleep(rig.Root, 60);

        Task poll = rig.Poll();
        await WatcherRig.WaitFor(() => Task.FromResult(rig.Calls().Count == 1), "the poll to start");
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await poll.WaitAsync(WatcherRig.Bound);

        Assert.Equal(StatusError.PollTimeout, rig.Status.Current.LastError?.Code);
        Assert.True(kills.Count >= 1, "the timeout did not kill the poll");
        int pid = Assert.Single(rig.Calls()).GetProperty("pid").GetInt32();
        await WatcherRig.WaitFor(() => Task.FromResult(!WatcherRig.IsRunning(pid)), "the poll to exit");
    }

    [Fact]
    public async Task CallerCancelWhileRunning_Kills()
    {
        Kills kills = new();
        using WatcherRig rig = new(pollOnClock: true, beforeKill: kills.Add);
        FakeJobPy.SetSleep(rig.Root, 60);
        using CancellationTokenSource caller = new();

        Task<PollOutcome> run = rig.Poller().RunAsync(rig.Root, caller.Token);
        await WatcherRig.WaitFor(() => Task.FromResult(rig.Calls().Count == 1), "the poll to start");
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(WatcherRig.Bound));
        Assert.True(kills.Count >= 1, "the cancel did not kill the poll");
        int pid = Assert.Single(rig.Calls()).GetProperty("pid").GetInt32();
        await WatcherRig.WaitFor(() => Task.FromResult(!WatcherRig.IsRunning(pid)), "the poll to exit");
    }
}
