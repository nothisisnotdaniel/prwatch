using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PRWatch;

/// <summary>The real review runner: one headless <c>claude -p "/factory pr --headless --project &lt;P&gt; &lt;n&gt;"</c>
/// run at a time with <see cref="ClaudeArgs.Argv"/> as its <see cref="ProcessStartInfo.ArgumentList"/> (no shell),
/// cwd the factory root, stdin closed and only <c>HOME</c>, <c>USER</c>, <c>TMPDIR</c>, <c>FACTORY_PRS_DIR</c>,
/// <c>FACTORY_PR_HEADLESS=1</c> and <c>PATH</c> (= <see cref="PollRunner.FixedPath"/>) in its environment. The
/// <c>claudePath</c> setting is resolved to its final target right before each start and only that target runs.
/// The stream-json output is read line by line and only figures are kept (cost, token counts, plan usage, the
/// credential's expiry and fixed codes); no text of the output, stderr or the prompt is stored or logged. A run
/// past the timeout (on the <see cref="TimeProvider"/>), with too much output or still going when the app stops
/// is killed with its process tree. Every run holds the update lock (<see cref="UpdateLock"/>) from before its
/// process starts to the end of its <c>finally</c>, touched every <see cref="LockTouchInterval"/> on the
/// <see cref="TimeProvider"/>, so RoomKit's updater never swaps the server under a review.</summary>
internal sealed partial class ClaudeReviewRunner : IReviewRunner, IDisposable
{
    public const int MaxLineBytes = 1024 * 1024;

    public const long MaxOutputBytes = 32L * 1024 * 1024;

    public const int MaxErrorBytes = 64 * 1024;

    public const int DefaultTimeoutSeconds = 3600;

    public const string PrsDirVariable = "FACTORY_PRS_DIR";

    public const string HeadlessVariable = "FACTORY_PR_HEADLESS";

    public const string BudgetSubtype = "error_max_budget_usd";

    public const int MaxModels = 20;

    public const int MaxModelFields = 20;

    /// <summary>How often a run sets the lock folder's mtime (the updater counts a lock past 60 min as stale).</summary>
    public static readonly TimeSpan LockTouchInterval = TimeSpan.FromMinutes(10);

    /// <summary>The <c>rate_limit_info.status</c> values that are not a limit.</summary>
    public static readonly string[] AllowedStatuses = ["allowed", "allowed_warning"];

    private readonly object gate = new();

    private readonly SettingsStore settings;

    private readonly StatusStore status;

    private readonly UsageLedger ledger;

    private readonly TimeProvider time;

    private readonly UpdateLock updateLock;

    private Run? current;

    private bool stopping;

    public ClaudeReviewRunner(SettingsStore settings, StatusStore status, UsageLedger ledger, TimeProvider time,
        UpdateLock updateLock, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(updateLock);
        this.settings = settings;
        this.status = status;
        this.ledger = ledger;
        this.time = time;
        this.updateLock = updateLock;
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }

    public bool IsBusy
    {
        get
        {
            lock (gate)
            {
                return current is not null;
            }
        }
    }

    /// <summary>The run timeout from the config value <c>RunTimeoutSeconds</c> (default 3600).</summary>
    public static TimeSpan TimeoutFrom(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        int seconds = configuration.GetValue("RunTimeoutSeconds", DefaultTimeoutSeconds);
        return TimeSpan.FromSeconds(seconds > 0 ? seconds : DefaultTimeoutSeconds);
    }

    /// <summary><c>job.py</c>'s default PR folder: <c>&lt;parent of root&gt;/&lt;root name&gt;-factory-prs</c>, or
    /// <c>null</c> for a root without a parent.</summary>
    public static string? PrsDir(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        string trimmed = Path.TrimEndingDirectorySeparator(root);
        string? parent = Path.GetDirectoryName(trimmed);
        string name = Path.GetFileName(trimmed);
        return parent is null || name.Length == 0 ? null : Path.Combine(parent, name + "-factory-prs");
    }

    /// <summary>Starts the run, or returns false and starts nothing: while a run is going or the app stops, while
    /// <c>enabled</c> is off, while the token brake holds (written to the status then), while the update lock is
    /// taken by anyone else (its holder written to the status then), for a project not in the discovered list, a number outside 1..999999999 or a head that
    /// is no sha, with no model for the mode (<c>model_unknown</c>) or no usable claude (<c>claude_not_found</c>).
    /// On true the run's <c>headSha</c> and <c>startedAt</c> are in the status and its start is in the usage
    /// ledger.</summary>
    public bool TryStart(ReviewPick pick)
    {
        ArgumentNullException.ThrowIfNull(pick);
        lock (gate)
        {
            if (current is not null || stopping)
            {
                return false;
            }

            Settings now = settings.Current;
            if (!now.Enabled
                || now.FactoryRoot is not { } root
                || !SettingsRules.IsFactoryRoot(root)
                || !PrRules.IsNumber(pick.Number)
                || !PrRules.IsSha(pick.HeadSha)
                || PrsDir(root) is not { } prs)
            {
                return false;
            }

            IReadOnlyList<LedgerEntry> entries = ledger.Entries;
            DateTimeOffset checkedAt = time.GetUtcNow();
            BrakeStatus brake = TokenBrake.Evaluate(status.Current.PlanUsage, entries, now, checkedAt);
            if (brake.Holding)
            {
                status.Update(previous => previous with { Brake = brake });
                return false;
            }

            FactoryInfo info = FactoryInfo.Read(root);
            if (!info.Projects.Contains(pick.Project, StringComparer.Ordinal))
            {
                return false;
            }

            string? mode = now.ReviewerMode ?? info.DefaultMode;
            string? model = mode is not null && info.ReviewerModels.TryGetValue(mode, out string? found) ? found : null;
            if (model is null || !ClaudeArgs.IsModel(model))
            {
                status.RecordError(StatusError.ModelUnknown);
                return false;
            }

            string? claude = now.ClaudePath is null ? null : SettingsRules.ResolveExecutable(now.ClaudePath);
            if (claude is null)
            {
                status.RecordError(StatusError.ClaudeNotFound);
                return false;
            }

            IReadOnlyList<string> argv;
            try
            {
                argv = ClaudeArgs.Argv(claude, pick.Project, pick.Number, model, now.MaxBudgetUsd, prs);
            }
            catch (ArgumentException)
            {
                status.RecordError(StatusError.RunFailed);
                return false;
            }

            if (!updateLock.TryAcquire())
            {
                string? holder = updateLock.Holder() ?? UpdateLock.Updater;
                status.Update(previous => previous with { UpdateLock = holder });
                return false;
            }

            Process? process = Start(argv, root, prs);
            if (process is null)
            {
                updateLock.Release();
                status.Update(previous => previous with
                {
                    UpdateLock = updateLock.Holder(),
                    LastError = new StatusError(StatusError.RunFailed, time.GetUtcNow()),
                });
                return false;
            }

            string key = PrRules.RunKey(pick.Project, pick.Number);
            DateTimeOffset startedAt = time.GetUtcNow();
            LedgerEntry entry = ledger.Start(startedAt);
            status.Update(previous => previous with
            {
                Runs = previous.Runs.SetItem(key, new RunStatus { HeadSha = pick.HeadSha, StartedAt = startedAt }),
                LastError = previous.LastError is { } error
                    && StatusError.RunCodes.Contains(error.Code, StringComparer.Ordinal)
                        ? null
                        : previous.LastError,
                UpdateLock = UpdateLock.Run,
            });
            Run run = new(process, key, pick.HeadSha, entry, new CancellationTokenSource(Timeout, time));
            run.Touch = time.CreateTimer(_ => updateLock.Touch(time.GetUtcNow()), null, LockTouchInterval,
                LockTouchInterval);
            current = run;
            run.Monitor = Task.Run(() => MonitorAsync(run));
            return true;
        }
    }

    /// <summary>Starts no new run and kills the running one (recorded as <c>stopped</c>), then waits until its end
    /// is written.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Run? run;
        lock (gate)
        {
            stopping = true;
            run = current;
        }

        if (run is null)
        {
            return;
        }

        run.End(RunStatus.Stopped);
        try
        {
            await run.Monitor!.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The host gave up waiting; the run is killed anyway.
        }
    }

    public void Dispose()
    {
        Run? run;
        lock (gate)
        {
            stopping = true;
            run = current;
        }

        run?.End(RunStatus.Stopped);
    }

    private static Process? Start(IReadOnlyList<string> argv, string root, string prs)
    {
        ProcessStartInfo start = new(argv[0])
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in argv.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach (string name in PollRunner.PassedVariables)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                start.Environment[name] = value;
            }
        }

        if (!start.Environment.ContainsKey("TMPDIR"))
        {
            start.Environment["TMPDIR"] = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        }

        start.Environment[PrsDirVariable] = prs;
        start.Environment[HeadlessVariable] = "1";
        start.Environment["PATH"] = PollRunner.FixedPath;
        Process process = new() { StartInfo = start };
        try
        {
            if (process.Start())
            {
                return process;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // A fixed code only.
        }

        process.Dispose();
        return null;
    }

    private async Task MonitorAsync(Run run)
    {
        StreamFigures figures = new();
        bool authInStderr = false;
        int? exitCode = null;
        bool failed = false;
        try
        {
            using CancellationTokenRegistration onTimeout = run.Timeout.Token.Register(() => run.End(RunStatus.Timeout));
            try
            {
                run.Process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Already gone.
            }

            Task<bool> stderr = MatchErrorAsync(run.Process.StandardError.BaseStream);
            await ReadOutputAsync(run.Process.StandardOutput.BaseStream, run, figures);
            authInStderr = await stderr;
            await run.Process.WaitForExitAsync(CancellationToken.None);
            exitCode = run.Process.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or ObjectDisposedException)
        {
            failed = true;
            run.End(RunStatus.Error);
        }
        finally
        {
            Finish(run, figures, authInStderr, exitCode, failed);
        }
    }

    private void Finish(Run run, StreamFigures figures, bool authInStderr, int? exitCode, bool failed)
    {
        try
        {
            run.Touch?.Dispose();
            updateLock.Release();
            DateTimeOffset now = time.GetUtcNow();
            bool auth = !failed && (figures.AuthInResult
                || (figures.Results == 0 && exitCode is not 0 && exitCode is not null && authInStderr));
            string result;
            if (run.Reason is { } reason)
            {
                result = reason;
            }
            else if (auth)
            {
                result = RunStatus.Auth;
            }
            else if (figures.Results > 0 && figures.LastSubtype == BudgetSubtype)
            {
                result = RunStatus.Budget;
            }
            else if (figures.Results > 0 && !figures.AnyError && exitCode == 0)
            {
                result = RunStatus.Ok;
            }
            else
            {
                result = RunStatus.Error;
            }

            bool? isError = figures.AnyError ? true : figures.Results > 0 ? false : null;
            ledger.End(run.Entry, now, figures.CostUsd);
            IReadOnlyList<LedgerEntry> entries = ledger.Entries;
            Settings brakeSettings = settings.Current;
            status.Update(previous =>
            {
                RunStatus started = previous.Runs.TryGetValue(run.Key, out RunStatus? found)
                    ? found
                    : new RunStatus { HeadSha = run.HeadSha };
                LoginStatus login = previous.Login;
                if (auth)
                {
                    login = login with { State = LoginStatus.NeedsLogin };
                }
                else if (figures.Results > 0)
                {
                    login = login with { State = LoginStatus.Ok };
                }

                if (figures.CredentialExpiresAt is { } expires)
                {
                    login = login with { CredentialExpiresAt = expires };
                }

                login = login with { ExpiresSoon = LoginStatus.IsExpiringSoon(login.CredentialExpiresAt, now) };
                return previous with
                {
                    Runs = previous.Runs.SetItem(run.Key, started with
                    {
                        EndedAt = now,
                        ExitCode = exitCode,
                        Result = result,
                        CostUsd = figures.CostUsd,
                        IsError = isError,
                        ModelUsage = figures.ModelUsage,
                    }),
                    Login = login,
                    Brake = TokenBrake.Evaluate(previous.PlanUsage, entries, brakeSettings, now),
                    UpdateLock = updateLock.Holder(),
                };
            });
        }
        finally
        {
            // Again, in case the end could not be written: the lock never outlives the run.
            run.Touch?.Dispose();
            updateLock.Release();
            run.Timeout.Dispose();
            run.Process.Dispose();
            lock (gate)
            {
                if (ReferenceEquals(current, run))
                {
                    current = null;
                }
            }
        }
    }

    /// <summary>Reads the output line by line: a line over <see cref="MaxLineBytes"/> is skipped, and past
    /// <see cref="MaxOutputBytes"/> in total the run ends as <c>output_too_large</c>.</summary>
    private async Task ReadOutputAsync(Stream stream, Run run, StreamFigures figures)
    {
        byte[] chunk = new byte[64 * 1024];
        using MemoryStream line = new();
        bool skipping = false;
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            total += read;
            if (total > MaxOutputBytes)
            {
                run.End(RunStatus.OutputTooLarge);
                return;
            }

            int start = 0;
            while (start < read)
            {
                int newline = Array.IndexOf(chunk, (byte)'\n', start, read - start);
                int end = newline < 0 ? read : newline;
                if (!skipping)
                {
                    if (line.Length + (end - start) > MaxLineBytes)
                    {
                        skipping = true;
                        line.SetLength(0);
                    }
                    else
                    {
                        line.Write(chunk, start, end - start);
                    }
                }

                if (newline < 0)
                {
                    break;
                }

                if (!skipping)
                {
                    Handle(line.GetBuffer(), (int)line.Length, figures);
                }

                line.SetLength(0);
                skipping = false;
                start = newline + 1;
            }
        }

        if (!skipping && line.Length > 0)
        {
            Handle(line.GetBuffer(), (int)line.Length, figures);
        }
    }

    /// <summary>Takes the figures of one stream-json line; a line that is no JSON object is ignored.</summary>
    private void Handle(byte[] buffer, int length, StreamFigures figures)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, length));
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object || ReadString(item, "type") is not { } type)
            {
                return;
            }

            if (type == "result")
            {
                TakeResult(item, figures);
            }
            else if (type == "rate_limit_event")
            {
                PlanUsage usage = ReadPlanUsage(item, time.GetUtcNow());
                status.Update(previous => previous with { PlanUsage = usage });
            }
        }
    }

    private static void TakeResult(JsonElement item, StreamFigures figures)
    {
        figures.Results++;
        bool isError = item.TryGetProperty("is_error", out JsonElement error) && error.ValueKind == JsonValueKind.True;
        figures.AnyError |= isError;
        if (isError && ReadString(item, "result") is { } text && AuthPattern().IsMatch(text))
        {
            figures.AuthInResult = true;
        }

        figures.CostUsd = ReadNumber(item, "total_cost_usd");
        figures.LastSubtype = ReadString(item, "subtype") == BudgetSubtype ? BudgetSubtype : null;
        figures.ModelUsage = item.TryGetProperty("modelUsage", out JsonElement usage) ? ReadModelUsage(usage) : null;
        figures.CredentialExpiresAt = item.TryGetProperty("credential_expires_at", out JsonElement expires)
            ? ReadTime(expires)
            : null;
    }

    /// <summary>The model names (as <see cref="ClaudeArgs.IsModel"/> accepts them) with their numeric fields only,
    /// at most <see cref="MaxModels"/> models and <see cref="MaxModelFields"/> fields each.</summary>
    private static Dictionary<string, IReadOnlyDictionary<string, double>>? ReadModelUsage(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Dictionary<string, IReadOnlyDictionary<string, double>> models = new(StringComparer.Ordinal);
        foreach (JsonProperty model in usage.EnumerateObject())
        {
            if (models.Count >= MaxModels)
            {
                break;
            }

            if (!ClaudeArgs.IsModel(model.Name) || model.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            Dictionary<string, double> counts = new(StringComparer.Ordinal);
            foreach (JsonProperty field in model.Value.EnumerateObject())
            {
                if (counts.Count >= MaxModelFields)
                {
                    break;
                }

                if (FieldPattern().IsMatch(field.Name)
                    && field.Value.ValueKind == JsonValueKind.Number
                    && field.Value.TryGetDouble(out double number)
                    && double.IsFinite(number))
                {
                    counts.TryAdd(field.Name, number);
                }
            }

            models.TryAdd(model.Name, counts);
        }

        return models;
    }

    private static PlanUsage ReadPlanUsage(JsonElement item, DateTimeOffset now)
    {
        UsageWindow? fiveHour = null;
        UsageWindow? sevenDay = null;
        string? state = null;
        if (item.TryGetProperty("rate_limit_info", out JsonElement info) && info.ValueKind == JsonValueKind.Object)
        {
            state = ReadString(info, "status");
            if (info.TryGetProperty("unifiedWindows", out JsonElement windows) && windows.ValueKind == JsonValueKind.Object)
            {
                fiveHour = ReadWindow(windows, "five_hour");
                sevenDay = ReadWindow(windows, "seven_day");
            }
        }

        return new PlanUsage
        {
            FiveHour = fiveHour,
            SevenDay = sevenDay,
            Limited = state is null || !AllowedStatuses.Contains(state, StringComparer.Ordinal),
            At = now,
        };
    }

    private static UsageWindow? ReadWindow(JsonElement windows, string name)
    {
        if (!windows.TryGetProperty(name, out JsonElement window) || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new UsageWindow
        {
            Utilization = ReadNumber(window, "utilization"),
            ResetsAt = window.TryGetProperty("resetsAt", out JsonElement resets) ? ReadTime(resets) : null,
        };
    }

    /// <summary>An ISO-8601 string, or a number of epoch seconds (milliseconds from 10^11 on).</summary>
    public static DateTimeOffset? ReadTime(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                    ? parsed.ToUniversalTime()
                    : null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
        {
            return null;
        }

        double milliseconds = Math.Abs(number) >= 1e11 ? number : number * 1000;
        if (milliseconds < -62_135_596_800_000 || milliseconds > 253_402_300_799_999)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
    }

    private static double? ReadNumber(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double number)
            && double.IsFinite(number)
                ? number
                : null;
    }

    private static string? ReadString(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>Reads stderr to its end so the child never blocks on a full pipe, keeps at most
    /// <see cref="MaxErrorBytes"/>, and only matches them against the auth pattern; the text is then thrown
    /// away.</summary>
    private static async Task<bool> MatchErrorAsync(Stream stream)
    {
        byte[] kept = new byte[MaxErrorBytes];
        int length = 0;
        byte[] chunk = new byte[8192];
        int read;
        try
        {
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                int take = Math.Min(read, MaxErrorBytes - length);
                Array.Copy(chunk, 0, kept, length, take);
                length += take;
            }

            return AuthPattern().IsMatch(Encoding.UTF8.GetString(kept, 0, length));
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            Array.Clear(kept, 0, length);
        }
    }

    /// <summary><c>prwatch_smoke.AUTH</c>.</summary>
    [GeneratedRegex(@"authenticat|oauth|not logged in|/login|\b401\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();

    /// <summary>One running process, its <c>runs</c> key and head, its ledger entry, its timeout and the first reason it was ended
    /// for (<c>timeout</c>, <c>output_too_large</c>, <c>stopped</c>).</summary>
    private sealed class Run
    {
        private string? reason;

        public Run(Process process, string key, string headSha, LedgerEntry entry, CancellationTokenSource timeout)
        {
            Process = process;
            Key = key;
            HeadSha = headSha;
            Entry = entry;
            Timeout = timeout;
        }

        public Process Process { get; }

        public string Key { get; }

        public string HeadSha { get; }

        /// <summary>The run's entry in the usage ledger.</summary>
        public LedgerEntry Entry { get; }

        public CancellationTokenSource Timeout { get; }

        public Task? Monitor { get; set; }

        /// <summary>The timer that touches the update lock while the run goes.</summary>
        public ITimer? Touch { get; set; }

        public string? Reason => Volatile.Read(ref reason);

        /// <summary>Records <paramref name="why"/> unless a reason is already set, and kills the process
        /// tree.</summary>
        public void End(string why)
        {
            Interlocked.CompareExchange(ref reason, why, null);
            try
            {
                Process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
                or NotSupportedException)
            {
                // Already gone (or disposed).
            }
        }
    }

    /// <summary>The figures of the stream so far: the last <c>result</c>'s values, whether any was an error and
    /// whether one was an auth error.</summary>
    private sealed class StreamFigures
    {
        public int Results { get; set; }

        public bool AnyError { get; set; }

        public bool AuthInResult { get; set; }

        public double? CostUsd { get; set; }

        public string? LastSubtype { get; set; }

        public Dictionary<string, IReadOnlyDictionary<string, double>>? ModelUsage { get; set; }

        public DateTimeOffset? CredentialExpiresAt { get; set; }
    }
}
