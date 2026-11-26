using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PRWatch.Tests;

/// <summary>A watcher built straight on its stores (no host), in its own temp folder: a test factory root with
/// the fake <c>job.py</c>, a data folder, the calls file outside the root, a <see cref="FakeClock"/>, a
/// <see cref="FakeReviewRunner"/> and a <see cref="UsageLedger"/> on the data folder. The settings start with the root and <c>quietMinutes</c> 0.</summary>
internal sealed class WatcherRig : IDisposable
{
    public const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public const string ShaC = "cccccccccccccccccccccccccccccccccccccccc";

    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <param name="pollOnClock">Runs the poll timeout on <see cref="Clock"/> instead of the system clock.</param>
    /// <param name="afterExit">The poll runner's test hook (<see cref="PollRunner.AfterExit"/>), given this rig and the
    /// timeout token.</param>
    /// <param name="readVariable">The poll runner's environment reader (<see cref="PollRunner.ReadVariable"/>);
    /// <c>null</c> keeps the real process environment.</param>
    /// <param name="beforeKill">The poll runner's kill hook (<see cref="PollRunner.BeforeKill"/>).</param>
    public WatcherRig(TimeSpan? timeout = null, string? searchPath = null, bool open = true, bool pollOnClock = false,
        Func<WatcherRig, CancellationToken, Task>? afterExit = null, Func<string, string?>? readVariable = null,
        Action? beforeKill = null)
    {
        BeforeKill = beforeKill;
        PollOnClock = pollOnClock;
        ReadVariable = readVariable;
        AfterExit = afterExit is null ? null : token => afterExit(this, token);
        Folder = Path.Combine(Path.GetTempPath(), "prwatch-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        Root = TestFactoryRoot.Create(Folder);
        CallsFile = Path.Combine(Folder, "poll-calls.jsonl");
        FakeJobPy.Write(Root, CallsFile);
        DataFolder = Path.Combine(Folder, "data");
        Directory.CreateDirectory(DataFolder);
        Timeout = timeout ?? TimeSpan.FromSeconds(60);
        SearchPath = searchPath ?? PollRunner.FixedPath;
        if (open)
        {
            Open();
            Change("{\"quietMinutes\":0}");
        }
    }

    public string Folder { get; }

    public string Root { get; }

    public string CallsFile { get; }

    public string DataFolder { get; }

    public TimeSpan Timeout { get; }

    public string SearchPath { get; }

    /// <summary>True when the poll timeout runs on <see cref="Clock"/>.</summary>
    public bool PollOnClock { get; }

    public Func<CancellationToken, Task>? AfterExit { get; }

    /// <summary>The poll runner's kill hook, or <c>null</c>.</summary>
    public Action? BeforeKill { get; }

    /// <summary>The poll runner's environment reader, or <c>null</c> for the real process environment.</summary>
    public Func<string, string?>? ReadVariable { get; }

    public FakeClock Clock { get; } = new(FakeClock.DefaultNow);

    public FakeReviewRunner Runner { get; } = new();

    public StatusStore Status { get; private set; } = null!;

    /// <summary>The usage ledger on <see cref="DataFolder"/>, rebuilt by <see cref="Open"/>.</summary>
    public UsageLedger Ledger { get; private set; } = null!;

    public SettingsStore Settings { get; private set; } = null!;

    public Watcher Watcher { get; private set; } = null!;

    /// <summary>(Re)builds the stores and the watcher on the same data folder, as a restart does.</summary>
    public void Open()
    {
        Watcher?.Dispose();
        Status = new StatusStore(DataFolder, Clock);
        Settings = new SettingsStore(DataFolder, Root, Status);
        Runner.Status = Status;
        Runner.Clock = Clock;
        Ledger = new UsageLedger(DataFolder, Status, Clock);
        Watcher = new Watcher(Settings, Status, Poller(), Runner, Ledger, Clock);
    }

    /// <summary>A poll runner with the rig's timeout, search path, clock choice, hooks and environment reader.</summary>
    public PollRunner Poller()
    {
        return new PollRunner(Timeout, SearchPath, PollOnClock ? Clock : null)
        {
            AfterExit = AfterExit,
            BeforeKill = BeforeKill,
            ReadVariable = ReadVariable ?? Environment.GetEnvironmentVariable,
        };
    }

    public void Change(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        SettingsChange change = Settings.Change(document.RootElement);
        Assert.NotNull(change.Saved);
    }

    public Task Poll()
    {
        return Watcher.PollOnceAsync(CancellationToken.None);
    }

    /// <summary>Sets the fake's output to these PRs and skipped entries.</summary>
    public void Output(IEnumerable<JsonObject> prs, IEnumerable<JsonObject>? skipped = null)
    {
        JsonObject output = new()
        {
            ["prs"] = new JsonArray([.. prs]),
            ["skipped"] = new JsonArray([.. skipped ?? []]),
        };
        FakeJobPy.SetOutput(Root, output.ToJsonString());
    }

    /// <summary>Writes <c>factory/jobs/&lt;id&gt;/state.json</c>.</summary>
    public void Job(string id, string content)
    {
        string folder = Path.Combine(Root, "factory", "jobs", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "state.json"), content);
    }

    public IReadOnlyList<JsonElement> Calls()
    {
        return FakeJobPy.Calls(CallsFile);
    }

    /// <summary>A PR as <c>job.py pr poll</c> prints it.</summary>
    public static JsonObject Pr(string project, int number, string head = ShaA, bool trusted = true,
        string? request = null, JsonObject? round = null)
    {
        return new JsonObject
        {
            ["project"] = project,
            ["number"] = number,
            ["title"] = "PR " + number,
            ["author"] = "someone",
            ["url"] = "https://github.com/owner/repo/pull/" + number,
            ["base"] = "develop",
            ["headSha"] = head,
            ["trusted"] = trusted,
            ["stage"] = null,
            ["request"] = request,
            ["round"] = round,
        };
    }

    public static JsonObject Round(int k, string head, bool current, bool clean = false)
    {
        return new JsonObject
        {
            ["k"] = k,
            ["headSha"] = head,
            ["current"] = current,
            ["tests"] = "pass",
            ["mergeable"] = true,
            ["commented"] = false,
            ["clean"] = clean,
            ["problems"] = new JsonArray(),
        };
    }

    /// <summary>True while a process with this id runs.</summary>
    public static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds, checking every 50 ms within <see cref="Bound"/>;
    /// fails with <paramref name="what"/> after that.</summary>
    public static async Task WaitFor(Func<Task<bool>> condition, string what)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(watch.Elapsed < Bound, "timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    public void Dispose()
    {
        Watcher?.Dispose();
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }
}
