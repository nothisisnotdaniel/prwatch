using System.Text.Json;

namespace PRWatch;

/// <summary>The watcher: while <c>enabled</c> is on it polls once at start and then every <c>pollMinutes</c> (read
/// fresh each time, on the container's <see cref="TimeProvider"/>), and at once when <c>enabled</c> is turned on.
/// Each poll (<see cref="PollOnceAsync"/>) stores the valid open PRs and the skipped projects of the allowed
/// projects (<see cref="AllowedProjects"/>: <c>factory</c> is an ordinary choice there) in the status and hands
/// at most one PR that needs a review to the <see cref="IReviewRunner"/>. The factory root is only read here:
/// <c>job.py pr poll</c> and the <c>factory/jobs/*/state.json</c> files.</summary>
internal sealed class Watcher : BackgroundService
{
    public const long MaxStateBytes = 64 * 1024;

    /// <summary>The job stages in which a project's checkout is busy.</summary>
    public static readonly string[] ActiveStages = ["spec", "build", "review", "approve"];

    private readonly SettingsStore settings;

    private readonly StatusStore status;

    private readonly PollRunner poll;

    private readonly IReviewRunner runner;

    private readonly UsageLedger ledger;

    private readonly TimeProvider time;

    private readonly SemaphoreSlim wake = new(0, 1);

    private readonly object notedGate = new();

    /// <summary>The recent requests, at most one per PR (<c>&lt;repo&gt;-&lt;n&gt;</c>, latest wins), each with
    /// its sequence number. Only in memory; every poll drops what it does not keep.</summary>
    private readonly Dictionary<string, NotedRequest> noted = new(StringComparer.Ordinal);

    private long notedSequence;

    private int pollNow;

    public Watcher(SettingsStore settings, StatusStore status, PollRunner poll, IReviewRunner runner,
        UsageLedger ledger, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(time);
        this.settings = settings;
        this.status = status;
        this.poll = poll;
        this.runner = runner;
        this.ledger = ledger;
        this.time = time;
        settings.Changed += OnSettingsChanged;
    }

    /// <summary>Polls once, stores the PRs, the skipped projects, the token brake (<see cref="TokenBrake.Evaluate"/>
    /// on the plan usage and the usage ledger, before the pick), the pick and <c>login.expiresSoon</c>, and hands
    /// the pick to the runner (which writes the run's start).
    /// A failed poll records its code and keeps <c>prs</c> and <c>lastPoll</c>; nothing is picked then, and the
    /// noted requests (<see cref="NoteRequest"/>) stay until the next successful poll drops them.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        Settings current = settings.Current;
        string? root = current.FactoryRoot;
        if (root is null || !SettingsRules.IsFactoryRoot(root))
        {
            RecordFailure(StatusError.PollFailed);
            return;
        }

        long pollStart = NotedSequence();
        using PollOutcome outcome = await poll.RunAsync(root, cancellationToken);
        if (outcome.Output is null)
        {
            RecordFailure(outcome.Error ?? StatusError.PollFailed);
            return;
        }

        DateTimeOffset now = time.GetUtcNow();
        FactoryInfo info = FactoryInfo.Read(root);
        HashSet<string> known = new(info.Projects, StringComparer.Ordinal);
        HashSet<string> allowed = AllowedProjects(info.Projects, current.Projects);
        JsonElement top = outcome.Output.RootElement;
        SortedSet<string> active = ActiveProjects(root, allowed);
        List<SkipStatus> skipped = ReadSkipped(top.GetProperty("skipped"), allowed);
        skipped.AddRange(active.Select(name => new SkipStatus { Project = name, Reason = SkipStatus.JobActive }));
        bool runnerBusy = runner.IsBusy;
        IReadOnlyList<LedgerEntry> entries = ledger.Entries;
        ReviewPick? pick = null;
        status.Update(previous =>
        {
            List<PrStatus> prs = ApplyNoted(ReadPrs(top.GetProperty("prs"), allowed, previous.Prs, now), pollStart);
            Status next = previous with
            {
                LastPoll = now,
                Login = previous.Login with
                {
                    ExpiresSoon = LoginStatus.IsExpiringSoon(previous.Login.CredentialExpiresAt, now),
                },
                Prs = prs,
                Skipped = skipped,
                Brake = TokenBrake.Evaluate(previous.PlanUsage, entries, current, now),
                Runs = previous.Runs.RemoveRange(
                    previous.Runs.Keys.Where(key => !known.Contains(PrRules.RunKeyProject(key))).ToList()),
                LastError = previous.LastError is { } error
                    && StatusError.PollCodes.Contains(error.Code, StringComparer.Ordinal)
                        ? null
                        : previous.LastError,
            };
            pick = runnerBusy ? null : Choose(next, current, active, now);
            return next with
            {
                Pick = pick is null
                    ? null
                    : new PickStatus { Project = pick.Project, Number = pick.Number, HeadSha = pick.HeadSha, At = now },
            };
        });

        if (pick is not null)
        {
            runner.TryStart(pick);
        }
    }

    /// <summary>The projects a poll keeps PRs, picks, skips and active jobs of: every discovered project (the
    /// factory's own <c>factory</c> included) when <paramref name="selected"/> is <c>null</c>, else only the
    /// discovered projects named in it. <c>factory</c> has no special case: left out of the list, its PRs are
    /// neither stored nor picked.</summary>
    public static HashSet<string> AllowedProjects(IEnumerable<string> discovered, IReadOnlyList<string>? selected)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        return new HashSet<string>(
            discovered.Where(name => selected is null || selected.Contains(name, StringComparer.Ordinal)),
            StringComparer.Ordinal);
    }

    /// <summary>The first PR in <c>prs</c> order that needs a run now, or <c>null</c> (also while the brake
    /// holds or the login is needed). A PR with a <c>review</c>, <c>merge</c> or <c>cancel</c> request is picked
    /// past the round, trust and same-head rules, but never during the quiet time or while a job of its project is
    /// active.</summary>
    public static ReviewPick? Choose(Status status, Settings settings, IReadOnlySet<string> activeProjects,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(activeProjects);
        if (status.Brake.Holding || status.Login.State == LoginStatus.NeedsLogin)
        {
            return null;
        }

        TimeSpan quiet = TimeSpan.FromMinutes(settings.QuietMinutes);
        foreach (PrStatus pr in status.Prs)
        {
            if (now - pr.HeadSeenAt < quiet || activeProjects.Contains(pr.Project))
            {
                continue;
            }

            // A request is the user's tap for this head: it is handed to the run even when the round is current,
            // the author is untrusted, auto-review is off or a run already ran for this head.
            bool requested = pr.Request is not null && PrRules.Requests.Contains(pr.Request, StringComparer.Ordinal);
            if (!requested
                && (pr.Round is { Current: true } || !(pr.Trusted && settings.AutoReviewTrusted)))
            {
                continue;
            }

            if (!requested
                && status.Runs.TryGetValue(PrRules.RunKey(pr.Project, pr.Number), out RunStatus? run)
                && run.HeadSha == pr.HeadSha)
            {
                continue;
            }

            return new ReviewPick(pr.Project, pr.Number, pr.HeadSha, pr.Trusted, pr.Request);
        }

        return null;
    }

    /// <summary>Notes a request just written by <c>POST /api/prs/&lt;action&gt;</c>, so that a poll that started
    /// before it (and so may print no <c>request</c>) still shows it for the same head. One entry per PR, the
    /// latest wins.</summary>
    public void NoteRequest(PrRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (notedGate)
        {
            notedSequence++;
            noted[PrRules.RunKey(request.Project, request.Number)] =
                new NotedRequest(request.Action, request.HeadSha, notedSequence);
        }
    }

    /// <summary>Wakes the loop for one poll at once (as turning <c>enabled</c> on does); the loop polls only while
    /// <c>enabled</c> is on.</summary>
    public void PollSoon()
    {
        Interlocked.Exchange(ref pollNow, 1);
        Wake();
    }

    public override void Dispose()
    {
        settings.Changed -= OnSettingsChanged;
        wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastStart = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            Settings current = settings.Current;
            bool forced = Interlocked.Exchange(ref pollNow, 0) == 1;
            TimeSpan interval = TimeSpan.FromMinutes(current.PollMinutes);
            if (!current.Enabled)
            {
                lastStart = null;
            }
            else if (forced || lastStart is null || time.GetUtcNow() - lastStart.Value >= interval)
            {
                lastStart = time.GetUtcNow();
                try
                {
                    await PollOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Never the exception's text: a fixed code only, and the loop goes on.
                    RecordFailure(StatusError.PollFailed);
                }

                continue;
            }

            DateTimeOffset? deadline = current.Enabled && lastStart is not null ? lastStart.Value + interval : null;
            if (deadline is { } due && due <= time.GetUtcNow())
            {
                continue;
            }

            await WaitAsync(deadline, stoppingToken);
        }
    }

    private long NotedSequence()
    {
        lock (notedGate)
        {
            return notedSequence;
        }
    }

    /// <summary>Sets the noted action on each PR of the poll whose request is newer than the poll's start and
    /// names the same head; drops every noted request that is not newer than the start, or whose PR is gone or
    /// has another head. A poll that started after a request was noted decides the value itself.</summary>
    private List<PrStatus> ApplyNoted(List<PrStatus> prs, long pollStart)
    {
        lock (notedGate)
        {
            if (noted.Count == 0)
            {
                return prs;
            }

            Dictionary<string, PrStatus> listed = new(StringComparer.Ordinal);
            foreach (PrStatus pr in prs)
            {
                listed.TryAdd(PrRules.RunKey(pr.Project, pr.Number), pr);
            }

            foreach ((string key, NotedRequest entry) in noted.ToList())
            {
                if (entry.Sequence <= pollStart
                    || !listed.TryGetValue(key, out PrStatus? pr)
                    || pr.HeadSha != entry.HeadSha)
                {
                    noted.Remove(key);
                }
            }

            return [.. prs.Select(pr =>
                noted.TryGetValue(PrRules.RunKey(pr.Project, pr.Number), out NotedRequest? entry)
                    && entry.HeadSha == pr.HeadSha
                    ? pr with { Request = entry.Action }
                    : pr)];
        }
    }

    private void RecordFailure(string code)
    {
        status.Update(previous => previous with { Pick = null, LastError = new StatusError(code, time.GetUtcNow()) });
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs change)
    {
        if (!change.Previous.Enabled && change.Current.Enabled)
        {
            Interlocked.Exchange(ref pollNow, 1);
        }

        Wake();
    }

    private void Wake()
    {
        try
        {
            if (wake.CurrentCount == 0)
            {
                wake.Release();
            }
        }
        catch (Exception exception) when (exception is SemaphoreFullException or ObjectDisposedException)
        {
            // Already woken, or the watcher is gone.
        }
    }

    /// <summary>Waits until <paramref name="deadline"/> on the clock (forever when <c>null</c>) or a settings
    /// change, whichever comes first. The clock is read again once the timer is armed, so a clock that moved past
    /// the deadline in between never leaves the loop waiting on a timer set too late.</summary>
    private async Task WaitAsync(DateTimeOffset? deadline, CancellationToken stoppingToken)
    {
        using CancellationTokenSource done = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task signal = wake.WaitAsync(done.Token);
        Task delay = deadline is { } due
            ? Task.Delay(Positive(due - time.GetUtcNow()), time, done.Token)
            : Task.Delay(Timeout.Infinite, done.Token);
        if (deadline is null || deadline.Value > time.GetUtcNow())
        {
            await Task.WhenAny(signal, delay);
        }

        await done.CancelAsync();
        try
        {
            await Task.WhenAll(signal, delay);
        }
        catch (OperationCanceledException)
        {
            // The other one, cancelled.
        }
    }

    private static TimeSpan Positive(TimeSpan span)
    {
        return span > TimeSpan.Zero ? span : TimeSpan.Zero;
    }

    /// <summary>The allowed projects that have a factory job in an active stage: each
    /// <c>factory/jobs/&lt;id&gt;/state.json</c> read through <see cref="SafeFileReader"/> (64 KiB cap); a job
    /// folder that is a symlink and an unreadable or invalid file are ignored.</summary>
    private static SortedSet<string> ActiveProjects(string root, IReadOnlySet<string> allowed)
    {
        SortedSet<string> active = new(StringComparer.Ordinal);
        DirectoryInfo jobs = new(Path.Combine(root, "factory", "jobs"));
        IEnumerable<DirectoryInfo> folders;
        try
        {
            if (!jobs.Exists || jobs.LinkTarget is not null)
            {
                return active;
            }

            folders = [.. jobs.EnumerateDirectories()];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return active;
        }

        foreach (DirectoryInfo folder in folders)
        {
            if (folder.LinkTarget is not null)
            {
                continue;
            }

            byte[]? bytes = SafeFileReader.ReadBytesAt(folder.FullName, "state.json", MaxStateBytes);
            if (bytes is null)
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(bytes);
                JsonElement state = document.RootElement;
                if (state.ValueKind == JsonValueKind.Object
                    && ReadString(state, "project") is { } project
                    && allowed.Contains(project)
                    && ReadString(state, "stage") is { } stage
                    && ActiveStages.Contains(stage, StringComparer.Ordinal))
                {
                    active.Add(project);
                }
            }
            catch (JsonException)
            {
                // An invalid state file is ignored.
            }
        }

        return active;
    }

    /// <summary>The skipped projects from <c>job.py</c>, each reason turned into a fixed code; the text itself is
    /// never kept.</summary>
    private static List<SkipStatus> ReadSkipped(JsonElement skipped, IReadOnlySet<string> allowed)
    {
        List<SkipStatus> list = [];
        foreach (JsonElement item in skipped.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || ReadString(item, "project") is not { } project
                || !allowed.Contains(project))
            {
                continue;
            }

            list.Add(new SkipStatus { Project = project, Reason = SkipReason(ReadString(item, "reason")) });
        }

        return list;
    }

    /// <summary>The fixed code for a <c>job.py</c> skip text.</summary>
    public static string SkipReason(string? text)
    {
        return text switch
        {
            "gh is not installed" => SkipStatus.GhMissing,
            "no origin remote" => SkipStatus.NoRemote,
            not null when text.StartsWith("gh pr list failed", StringComparison.Ordinal) => SkipStatus.GhFailed,
            _ => SkipStatus.Unknown,
        };
    }

    /// <summary>The valid PRs of the poll, sorted by project (ordinal) and number, at most
    /// <see cref="PrRules.MaxPrs"/>, each with its <c>headSeenAt</c> kept from <paramref name="previous"/> while
    /// the head is the same.</summary>
    private static List<PrStatus> ReadPrs(JsonElement prs, IReadOnlySet<string> allowed,
        IReadOnlyList<PrStatus> previous, DateTimeOffset now)
    {
        Dictionary<string, PrStatus> seen = new(StringComparer.Ordinal);
        foreach (PrStatus pr in previous)
        {
            seen.TryAdd(PrRules.RunKey(pr.Project, pr.Number), pr);
        }

        Dictionary<string, PrStatus> kept = new(StringComparer.Ordinal);
        foreach (JsonElement item in prs.EnumerateArray())
        {
            PrStatus? pr = ReadPr(item, allowed, now);
            if (pr is null)
            {
                continue;
            }

            string key = PrRules.RunKey(pr.Project, pr.Number);
            if (seen.TryGetValue(key, out PrStatus? before) && before.HeadSha == pr.HeadSha)
            {
                pr = pr with { HeadSeenAt = before.HeadSeenAt };
            }

            kept.TryAdd(key, pr);
        }

        return [.. kept.Values
            .OrderBy(pr => pr.Project, StringComparer.Ordinal)
            .ThenBy(pr => pr.Number)
            .Take(PrRules.MaxPrs)];
    }

    /// <summary>One PR when it keeps every rule, else <c>null</c>.</summary>
    private static PrStatus? ReadPr(JsonElement item, IReadOnlySet<string> allowed, DateTimeOffset now)
    {
        if (item.ValueKind != JsonValueKind.Object
            || ReadString(item, "project") is not { } project
            || !PrRules.IsProjectName(project)
            || !allowed.Contains(project)
            || !item.TryGetProperty("number", out JsonElement numberValue)
            || numberValue.ValueKind != JsonValueKind.Number
            || !numberValue.TryGetInt32(out int number)
            || !PrRules.IsNumber(number)
            || ReadString(item, "headSha") is not { } headSha
            || !PrRules.IsSha(headSha)
            || ReadBool(item, "trusted") is not { } trusted
            || !ReadRequest(item, out string? request)
            || !ReadRound(item, out RoundStatus? round))
        {
            return null;
        }

        string? url = ReadString(item, "url");
        return new PrStatus
        {
            Project = project,
            Number = number,
            Title = PrRules.Cap(ReadString(item, "title") ?? "", PrRules.MaxTitle),
            Author = PrRules.Cap(ReadString(item, "author") ?? "", PrRules.MaxAuthor),
            Url = PrRules.IsPullUrl(url, number) ? url : null,
            HeadSha = headSha,
            HeadSeenAt = now,
            Trusted = trusted,
            Request = request,
            Round = round,
            Stage = ReadString(item, "stage") is { } stage && PrRules.IsStage(stage) ? stage : null,
        };
    }

    private static bool ReadRequest(JsonElement item, out string? request)
    {
        request = null;
        if (!item.TryGetProperty("request", out JsonElement value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        request = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return request is not null && PrRules.IsRequest(request);
    }

    private static bool ReadRound(JsonElement item, out RoundStatus? round)
    {
        round = null;
        if (!item.TryGetProperty("round", out JsonElement value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("k", out JsonElement k)
            || k.ValueKind != JsonValueKind.Number
            || !k.TryGetInt32(out int number)
            || ReadString(value, "headSha") is not { } headSha
            || !PrRules.IsSha(headSha)
            || ReadBool(value, "current") is not { } current
            || ReadBool(value, "clean") is not { } clean)
        {
            return false;
        }

        string? tests = ReadString(value, "tests");
        round = new RoundStatus
        {
            K = number,
            HeadSha = headSha,
            Current = current,
            Clean = clean,
            Tests = tests is not null && PrRules.IsTests(tests) ? tests : null,
            Mergeable = ReadBool(value, "mergeable"),
        };
        return true;
    }

    private static string? ReadString(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? ReadBool(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}

/// <summary>A request noted by <see cref="Watcher.NoteRequest"/>: its action, the head it named and its sequence
/// number.</summary>
internal sealed record NotedRequest(string Action, string HeadSha, long Sequence);
