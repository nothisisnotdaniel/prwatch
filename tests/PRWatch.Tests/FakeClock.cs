namespace PRWatch.Tests;

/// <summary>A clock that stays at a fixed time until a test moves it. Its timers (<see cref="CreateTimer"/>, which
/// <c>Task.Delay(TimeSpan, TimeProvider, ...)</c> uses) fire only when <see cref="Advance"/> reaches their due time,
/// each at most once per call (a periodic timer is then due again one period after the new time); a timer due at
/// once (zero due time) fires right away on the thread pool.</summary>
internal sealed class FakeClock : TimeProvider
{
    public static readonly DateTimeOffset DefaultNow = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    private readonly object gate = new();

    private readonly List<FakeTimer> timers = [];

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

    /// <summary>Called with the due time before a new timer is armed, so a test can move the clock in exactly that
    /// gap.</summary>
    public Action<TimeSpan>? TimerCreating { get; set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        TimerCreating?.Invoke(dueTime);
        FakeTimer timer = new(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due = [];
        lock (gate)
        {
            now += by;
            foreach (FakeTimer timer in timers)
            {
                if (timer.Due is { } at && at <= now)
                {
                    timer.Due = timer.Period is { } period ? now + period : null;
                    due.Add(timer);
                }
            }
        }

        foreach (FakeTimer timer in due)
        {
            timer.Fire();
        }
    }

    private void Arm(FakeTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        bool fireNow;
        lock (gate)
        {
            TimeSpan? every = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? null : period;
            timer.Period = every;
            fireNow = dueTime == TimeSpan.Zero;
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                timer.Due = null;
            }
            else
            {
                timer.Due = fireNow ? (every is { } next ? now + next : null) : now + dueTime;
            }
        }

        if (fireNow)
        {
            ThreadPool.QueueUserWorkItem(_ => timer.Fire());
        }
    }

    private void Remove(FakeTimer timer)
    {
        lock (gate)
        {
            timers.Remove(timer);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeClock clock;

        private readonly TimerCallback callback;

        private readonly object? state;

        private int disposed;

        public FakeTimer(FakeClock clock, TimerCallback callback, object? state)
        {
            this.clock = clock;
            this.callback = callback;
            this.state = state;
        }

        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Volatile.Read(ref disposed) == 1)
            {
                return false;
            }

            clock.Arm(this, dueTime, period);
            return true;
        }

        public void Fire()
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                callback(state);
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref disposed, 1);
            clock.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
