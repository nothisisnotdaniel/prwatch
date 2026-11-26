using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PRWatch.Tests;

/// <summary>The watcher (backlog item 3): the poll process, its output rules, the stored PRs and skips, the pick,
/// the runner hand-off, the loop and the <c>runs</c> keys. Each test has its own temp folder, factory root, fake
/// <c>job.py</c> and clock; nothing writes into the root.</summary>
public sealed class WatcherTests
{
    private const string Sha = WatcherRig.ShaA;

    // Variables that /usr/bin/python3 on macOS (the Command Line Tools shim) adds itself.
    private static readonly string[] InterpreterVariables =
        ["SDKROOT", "LC_CTYPE", "__CF_USER_TEXT_ENCODING", "CPATH", "LIBRARY_PATH", "MANPATH"];

    // --- AC-1: the process -----------------------------------------------------------------------

    [Fact]
    public async Task Poll_RunsTheFirstPython3OnTheFixedPath_WithTheFixedArgvCwdStdinAndEnvironment()
    {
        Environment.SetEnvironmentVariable("SECRET_X", "do-not-pass");
        using WatcherRig rig = new();

        await rig.Poll();

        string expected = PollRunner.FixedPath.Split(':')
            .Select(folder => Path.Combine(folder, "python3"))
            .First(File.Exists);
        Assert.Equal(expected, PollRunner.FindPython());
        JsonElement call = Assert.Single(rig.Calls());
        string[] argv = [.. call.GetProperty("argv").EnumerateArray().Select(item => item.GetString()!)];
        Assert.Equal([Path.Combine(rig.Root, "factory", "tools", "job.py"), "pr", "poll"], argv);
        Assert.True(call.GetProperty("cwdIsRoot").GetBoolean());
        Assert.Equal("", call.GetProperty("stdin").GetString());
        JsonElement env = call.GetProperty("env");
        Assert.Equal(PollRunner.FixedPath, env.GetProperty("PATH").GetString());
        foreach (string name in PollRunner.PassedVariables)
        {
            Assert.Equal(Environment.GetEnvironmentVariable(name), env.GetProperty(name).GetString());
        }

        Assert.False(env.TryGetProperty("SECRET_X", out _));
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = (string)entry.Key;
            if (name is "HOME" or "USER" or "TMPDIR" or "PATH" || InterpreterVariables.Contains(name))
            {
                continue;
            }

            Assert.False(env.TryGetProperty(name, out _), "passed on: " + name);
        }

        Assert.Null(rig.Status.Current.LastError);
    }

    [Fact]
    public async Task Poll_WithAPathWithoutPython3_RunsNothing_AndRecordsPythonMissing()
    {
        string empty = Path.Combine(Path.GetTempPath(), "prwatch-nopython-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            using WatcherRig rig = new(searchPath: empty);

            await rig.Poll();

            Assert.Null(PollRunner.FindPython(empty));
            Assert.False(File.Exists(rig.CallsFile));
            Assert.Equal(StatusError.PythonMissing, rig.Status.Current.LastError?.Code);
            Assert.Null(rig.Status.Current.LastPoll);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    // --- AC-2: failures --------------------------------------------------------------------------

    [Theory]
    [InlineData("exit", StatusError.PollFailed)]
    [InlineData("garbage", StatusError.PollOutputInvalid)]
    [InlineData("array", StatusError.PollOutputInvalid)]
    [InlineData("no-skipped", StatusError.PollOutputInvalid)]
    [InlineData("prs-object", StatusError.PollOutputInvalid)]
    [InlineData("huge", StatusError.PollOutputInvalid)]
    public async Task FailedPoll_RecordsItsCode_KeepsPrsAndLastPoll_AndPicksNothing(string kind, string code)
    {
        using WatcherRig rig = new();
        rig.Output([WatcherRig.Pr("Alpha", 1)]);
        await rig.Poll();
        Status before = rig.Status.Current;
        Assert.NotNull(before.Pick);
        Assert.Single(rig.Runner.Calls);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        switch (kind)
        {
            case "exit":
                FakeJobPy.SetExit(rig.Root, 3);
                break;
            case "garbage":
                FakeJobPy.SetOutput(rig.Root, "not json at all {");
                break;
            case "array":
                FakeJobPy.SetOutput(rig.Root, "[]");
                break;
            case "no-skipped":
                FakeJobPy.SetOutput(rig.Root, "{\"prs\":[]}");
                break;
            case "prs-object":
                FakeJobPy.SetOutput(rig.Root, "{\"prs\":{},\"skipped\":[]}");
                break;
            default:
                FakeJobPy.SetOutput(rig.Root,
                    "{\"prs\":[],\"skipped\":[],\"pad\":\"" + new string('x', PollRunner.MaxOutputBytes) + "\"}");
                break;
        }

        await rig.Poll();

        Status after = rig.Status.Current;
        Assert.Equal(code, after.LastError?.Code);
        Assert.Equal(rig.Clock.GetUtcNow(), after.LastError?.At);
        Assert.Equal(before.LastPoll, after.LastPoll);
        Assert.Equal(before.Prs, after.Prs);
        Assert.Null(after.Pick);
        Assert.Single(rig.Runner.Calls);
        Assert.Equal(2, rig.Calls().Count);
    }

    [Fact]
    public async Task Poll_OverTheTimeout_IsKilledWithItsTree_AndGivesPollTimeout()
    {
        using WatcherRig rig = new(timeout: TimeSpan.FromSeconds(1));
        FakeJobPy.SetSleep(rig.Root, 60);
        Stopwatch watch = Stopwatch.StartNew();

        await rig.Poll();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(StatusError.PollTimeout, rig.Status.Current.LastError?.Code);
        Assert.Null(rig.Status.Current.LastPoll);
        int pid = Assert.Single(rig.Calls()).GetProperty("pid").GetInt32();
        await WatcherRig.WaitFor(() => Task.FromResult(!WatcherRig.IsRunning(pid)), "the poll to exit");
    }

    [Fact]
    public void PollTimeout_ComesFromTheConfig_WithADefaultOf120Seconds()
    {
        IConfiguration none = new ConfigurationBuilder().Build();
        IConfiguration one = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PollTimeoutSeconds"] = "1" })
            .Build();

        Assert.Equal(TimeSpan.FromSeconds(120), PollRunner.FromConfiguration(none).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(1), PollRunner.FromConfiguration(one).Timeout);
    }

    [Fact]
    public async Task Stderr_IsNeverStored()
    {
        using WatcherRig rig = new();
        rig.Output([WatcherRig.Pr("Alpha", 1)]);

        await rig.Poll();

        string file = File.ReadAllText(rig.Status.Path);
        Assert.DoesNotContain("FAKE-STDERR-MARKER", file, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE-STDERR-MARKER", JsonSerializer.Serialize(rig.Status.Current, PrWatchJson.Options),
            StringComparison.Ordinal);
    }

    // --- AC-3: stored PRs ------------------------------------------------------------------------

    [Fact]
    public async Task Poll_KeepsOnlyValidPrs_SortedAndCapped_WithCleanedText()
    {
        using WatcherRig rig = new();
        rig.Change("{\"projects\":[\"Alpha\"]}");
        JsonObject longText = WatcherRig.Pr("Alpha", 10);
        longText["title"] = new string('t', 250);
        longText["author"] = new string('a', 150);
        longText["url"] = "https://github.com/owner/repo/pull/11";
        JsonObject httpUrl = WatcherRig.Pr("Alpha", 3);
        httpUrl["url"] = "http://github.com/owner/repo/pull/3";
        JsonObject suffixUrl = WatcherRig.Pr("Alpha", 4);
        suffixUrl["url"] = "https://github.com/owner/repo/pull/4/files";
        JsonObject noTitle = WatcherRig.Pr("factory", 1, round: WatcherRig.Round(2, WatcherRig.ShaB, current: false));
        noTitle["title"] = 5;
        noTitle.Remove("author");
        List<JsonObject> prs =
        [
            longText, httpUrl, suffixUrl, noTitle,
            WatcherRig.Pr("Alpha", 2, request: "merge"),
            WatcherRig.Pr("Beta", 1),
            WatcherRig.Pr("Gamma", 1),
            WatcherRig.Pr("NotAProject", 1),
            WatcherRig.Pr("Alpha", 20, head: Sha.ToUpperInvariant()),
            WatcherRig.Pr("Alpha", 21, head: Sha[..39]),
            Set(WatcherRig.Pr("Alpha", 22), "number", 0),
            Set(WatcherRig.Pr("Alpha", 23), "number", 1_000_000_000),
            Set(WatcherRig.Pr("Alpha", 24), "number", "24"),
            Set(WatcherRig.Pr("Alpha", 25), "number", 25.5),
            Set(WatcherRig.Pr("Alpha", 26), "trusted", "yes"),
            Set(WatcherRig.Pr("Alpha", 27), "request", "approve"),
            Without(WatcherRig.Pr("Alpha", 28), "request"),
            Without(WatcherRig.Pr("Alpha", 29), "round"),
            WatcherRig.Pr("Alpha", 30, round: WatcherRig.Round(1, "nope", current: false)),
            WatcherRig.Pr("Alpha", 31, round: Without(WatcherRig.Round(1, Sha, current: false), "current")),
            WatcherRig.Pr("Alpha", 32, round: Set(WatcherRig.Round(1, Sha, current: false), "k", "1")),
            Without(WatcherRig.Pr("Alpha", 33), "trusted"),
            Set(WatcherRig.Pr("Alpha", 34), "project", null),
        ];
        rig.Output(prs);

        await rig.Poll();

        Status status = rig.Status.Current;
        Assert.Equal(rig.Clock.GetUtcNow(), status.LastPoll);
        Assert.Equal(
            ["Alpha-2", "Alpha-3", "Alpha-4", "Alpha-10", "factory-1"],
            status.Prs.Select(pr => PrRules.RunKey(pr.Project, pr.Number)));
        PrStatus capped = status.Prs[3];
        Assert.Equal(new string('t', 200), capped.Title);
        Assert.Equal(new string('a', 100), capped.Author);
        Assert.Null(capped.Url);
        Assert.Null(status.Prs[1].Url);
        Assert.Null(status.Prs[2].Url);
        Assert.Equal("https://github.com/owner/repo/pull/2", status.Prs[0].Url);
        Assert.Equal("merge", status.Prs[0].Request);
        PrStatus factory = status.Prs[4];
        Assert.Equal("", factory.Title);
        Assert.Equal("", factory.Author);
        Assert.Equal(new RoundStatus { K = 2, HeadSha = WatcherRig.ShaB, Current = false, Clean = false }, factory.Round);
        Assert.Equal(rig.Clock.GetUtcNow(), factory.HeadSeenAt);
        Assert.True(factory.Trusted);
    }

    [Fact]
    public async Task Poll_KeepsAtMost500Prs()
    {
        using WatcherRig rig = new();
        rig.Output(Enumerable.Range(1, 600).Reverse().Select(number => WatcherRig.Pr("Beta", number)));

        await rig.Poll();

        Assert.Equal(500, rig.Status.Current.Prs.Count);
        Assert.Equal(Enumerable.Range(1, 500), rig.Status.Current.Prs.Select(pr => pr.Number));
    }

    [Fact]
    public async Task Poll_ClearsPollErrors_ButKeepsOthers()
    {
        using WatcherRig rig = new();
        FakeJobPy.SetExit(rig.Root, 1);
        await rig.Poll();
        Assert.Equal(StatusError.PollFailed, rig.Status.Current.LastError?.Code);
        File.Delete(Path.Combine(rig.Root, "factory", "tools", "poll-exit"));

        await rig.Poll();
        Assert.Null(rig.Status.Current.LastError);

        rig.Status.RecordError(StatusError.SettingsUnreadable);
        await rig.Poll();
        Assert.Equal(StatusError.SettingsUnreadable, rig.Status.Current.LastError?.Code);
    }

    [Fact]
    public async Task Poll_WithAnInvalidRoot_RunsNothing_AndRecordsPollFailed()
    {
        using WatcherRig rig = new();
        File.Delete(Path.Combine(rig.Root, "factory", "modes.json"));

        await rig.Poll();

        Assert.False(File.Exists(rig.CallsFile));
        Assert.Equal(StatusError.PollFailed, rig.Status.Current.LastError?.Code);
    }

    // --- AC-4: headSeenAt ------------------------------------------------------------------------

    [Fact]
    public async Task HeadSeenAt_IsTheFirstPollOfTheHead_KeptWhileTheHeadStays()
    {
        using WatcherRig rig = new();
        DateTimeOffset first = rig.Clock.GetUtcNow();
        rig.Output([WatcherRig.Pr("Alpha", 1), WatcherRig.Pr("Beta", 1)]);
        await rig.Poll();

        rig.Clock.Advance(TimeSpan.FromMinutes(3));
        rig.Output([WatcherRig.Pr("Alpha", 1), WatcherRig.Pr("Beta", 1, head: WatcherRig.ShaB)]);
        await rig.Poll();

        Assert.Equal(first, rig.Status.Current.Prs[0].HeadSeenAt);
        Assert.Equal(first.AddMinutes(3), rig.Status.Current.Prs[1].HeadSeenAt);

        rig.Clock.Advance(TimeSpan.FromMinutes(3));
        rig.Open();
        await rig.Poll();
        Assert.Equal(first, rig.Status.Current.Prs[0].HeadSeenAt);
        Assert.Equal(first.AddMinutes(3), rig.Status.Current.Prs[1].HeadSeenAt);
        Assert.Equal(first.AddMinutes(6), rig.Status.Current.LastPoll);
    }

    [Fact]
    public async Task HeadSeenAt_SurvivesARestartOfTheHost()
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string root = TestFactoryRoot.Create(folder);
            string calls = Path.Combine(folder, "poll-calls.jsonl");
            FakeJobPy.Write(root, calls);
            FakeJobPy.SetOutput(root, "{\"prs\":[" + WatcherRig.Pr("Alpha", 7).ToJsonString() + "],\"skipped\":[]}");
            string hash = TestFactoryRoot.Hash(root);
            using (PrWatchFactory first = new(folder, ownsFolder: false) { FactoryRoot = root })
            {
                await first.Services.GetRequiredService<Watcher>().PollOnceAsync(CancellationToken.None);
            }

            using PrWatchFactory second = new(folder, ownsFolder: false) { FactoryRoot = root };
            second.Clock.Advance(TimeSpan.FromMinutes(30));
            using HttpClient client = second.CreateTokenClient();
            await second.Services.GetRequiredService<Watcher>().PollOnceAsync(CancellationToken.None);
            using JsonDocument body = JsonDocument.Parse(await client.GetStringAsync("/api/status"));

            Assert.Equal("2026-11-26T10:30:00.000Z", body.RootElement.GetProperty("lastPoll").GetString());
            JsonElement pr = Assert.Single(body.RootElement.GetProperty("prs").EnumerateArray());
            Assert.Equal("2026-11-26T10:00:00.000Z", pr.GetProperty("headSeenAt").GetString());
            Assert.Equal(hash, TestFactoryRoot.Hash(root));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // --- AC-5: skipped ---------------------------------------------------------------------------

    [Fact]
    public async Task Skipped_HoldsFixedCodes_AndJobActiveFromTheStateFiles()
    {
        using WatcherRig rig = new();
        rig.Output([], [
            Skip("Alpha", "gh is not installed"),
            Skip("Beta", "no origin remote"),
            Skip("factory", "gh pr list failed: HTTP 401 secret-ish text"),
            Skip("Alpha", "something new"),
            new JsonObject { ["project"] = "Beta", ["reason"] = 3 },
            Skip("Gamma", "no origin remote"),
            new JsonObject { ["reason"] = "no origin remote" },
        ]);
        rig.Job("1-a", "{\"project\":\"Alpha\",\"stage\":\"build\"}");
        rig.Job("2-b", "{\"project\":\"Beta\",\"stage\":\"done\"}");
        rig.Job("3-c", "{\"project\":\"Beta\",\"stage\":");
        rig.Job("4-d", "{\"project\":\"factory\",\"stage\":\"approve\"}");
        rig.Job("5-e", "{\"project\":\"Beta\",\"stage\":\"review\",\"pad\":\"" + new string('x', 70 * 1024) + "\"}");
        rig.Job("6-f", "{\"project\":\"Gamma\",\"stage\":\"spec\"}");
        rig.Job("7-g", "[\"Beta\",\"spec\"]");

        await rig.Poll();

        Assert.Equal(
            [
                ("Alpha", "gh_missing"), ("Beta", "no_remote"), ("factory", "gh_failed"), ("Alpha", "unknown"),
                ("Beta", "unknown"), ("Alpha", "job_active"), ("factory", "job_active"),
            ],
            rig.Status.Current.Skipped.Select(skip => (skip.Project, skip.Reason)));
        string file = File.ReadAllText(rig.Status.Path);
        Assert.DoesNotContain("gh pr list failed", file, StringComparison.Ordinal);
        Assert.DoesNotContain("something new", file, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("spec", true)]
    [InlineData("build", true)]
    [InlineData("review", true)]
    [InlineData("approve", true)]
    [InlineData("done", false)]
    [InlineData("merged", false)]
    public async Task JobActive_OnlyForTheBusyStages(string stage, bool active)
    {
        using WatcherRig rig = new();
        rig.Job("9-x", "{\"project\":\"Beta\",\"stage\":\"" + stage + "\"}");

        await rig.Poll();

        Assert.Equal(active, rig.Status.Current.Skipped.Contains(new SkipStatus { Project = "Beta", Reason = "job_active" }));
    }

    // --- AC-6/7: the pick and the runner ---------------------------------------------------------

    [Theory]
    [InlineData("trusted", true)]
    [InlineData("round-current", false)]
    [InlineData("round-stale", true)]
    [InlineData("quiet-not-met", false)]
    [InlineData("quiet-met", true)]
    [InlineData("untrusted", false)]
    [InlineData("untrusted-review-request", true)]
    [InlineData("untrusted-merge-request", false)]
    [InlineData("auto-review-off", false)]
    [InlineData("job-active", false)]
    [InlineData("same-head-ran", false)]
    [InlineData("same-head-ran-review-request", true)]
    [InlineData("other-head-ran", true)]
    [InlineData("runner-busy", false)]
    [InlineData("brake-holding", false)]
    [InlineData("needs-login", false)]
    public async Task Pick_FollowsEachRule(string rule, bool picked)
    {
        using WatcherRig rig = new();
        JsonObject pr = rule switch
        {
            "round-current" => WatcherRig.Pr("Alpha", 1, round: WatcherRig.Round(1, Sha, current: true)),
            "round-stale" => WatcherRig.Pr("Alpha", 1, round: WatcherRig.Round(1, WatcherRig.ShaB, current: false)),
            "untrusted" => WatcherRig.Pr("Alpha", 1, trusted: false),
            "untrusted-review-request" => WatcherRig.Pr("Alpha", 1, trusted: false, request: "review"),
            "untrusted-merge-request" => WatcherRig.Pr("Alpha", 1, trusted: false, request: "merge"),
            "same-head-ran-review-request" => WatcherRig.Pr("Alpha", 1, request: "review"),
            _ => WatcherRig.Pr("Alpha", 1),
        };
        rig.Output([pr]);
        switch (rule)
        {
            case "quiet-not-met":
            case "quiet-met":
                rig.Change("{\"quietMinutes\":10}");
                break;
            case "auto-review-off":
                rig.Change("{\"autoReviewTrusted\":false}");
                break;
            case "job-active":
                rig.Job("1-a", "{\"project\":\"Alpha\",\"stage\":\"review\"}");
                break;
            case "same-head-ran":
            case "same-head-ran-review-request":
                SetRun(rig, "Alpha-1", Sha);
                break;
            case "other-head-ran":
                SetRun(rig, "Alpha-1", WatcherRig.ShaB);
                break;
            case "runner-busy":
                rig.Runner.IsBusy = true;
                break;
            case "brake-holding":
                rig.Status.Update(status => status with { Brake = new BrakeStatus { Holding = true, Reason = "x" } });
                break;
            case "needs-login":
                rig.Status.Update(status => status with { Login = new LoginStatus { State = LoginStatus.NeedsLogin } });
                break;
            default:
                break;
        }

        string hash = TestFactoryRoot.Hash(rig.Root);
        if (rule == "quiet-met")
        {
            await rig.Poll();
            Assert.Empty(rig.Runner.Calls);
            rig.Clock.Advance(TimeSpan.FromMinutes(10));
        }

        await rig.Poll();

        Status status = rig.Status.Current;
        Assert.Equal(hash, TestFactoryRoot.Hash(rig.Root));
        if (picked)
        {
            ReviewPick call = Assert.Single(rig.Runner.Calls);
            Assert.Equal(("Alpha", 1, Sha), (call.Project, call.Number, call.HeadSha));
            Assert.Equal(
                new PickStatus { Project = "Alpha", Number = 1, HeadSha = Sha, At = rig.Clock.GetUtcNow() },
                status.Pick);
            Assert.Equal(Sha, status.Runs["Alpha-1"].HeadSha);
            Assert.Equal(rig.Clock.GetUtcNow(), status.Runs["Alpha-1"].StartedAt);
        }
        else
        {
            Assert.Empty(rig.Runner.Calls);
            Assert.Null(status.Pick);
        }
    }

    [Fact]
    public async Task Pick_TakesOnlyTheFirstEligiblePr_AndCallsTheRunnerOnce()
    {
        using WatcherRig rig = new();
        rig.Output([
            WatcherRig.Pr("Beta", 1),
            WatcherRig.Pr("Alpha", 5),
            WatcherRig.Pr("Alpha", 2, round: WatcherRig.Round(1, Sha, current: true)),
        ]);

        await rig.Poll();

        ReviewPick call = Assert.Single(rig.Runner.Calls);
        Assert.Equal(new ReviewPick("Alpha", 5, Sha, true, null), call);
        Assert.Equal(["Alpha-5"], rig.Status.Current.Runs.Keys);
    }

    [Fact]
    public async Task Runner_ReturningFalse_LeavesRunsAsTheyWere()
    {
        using WatcherRig rig = new();
        rig.Runner.Result = false;
        SetRun(rig, "Alpha-1", WatcherRig.ShaB);
        RunStatus before = rig.Status.Current.Runs["Alpha-1"];
        rig.Output([WatcherRig.Pr("Alpha", 1)]);

        await rig.Poll();

        Assert.Single(rig.Runner.Calls);
        Assert.Same(before, rig.Status.Current.Runs["Alpha-1"]);
        Assert.Equal("Alpha", rig.Status.Current.Pick?.Project);
    }

    [Fact]
    public async Task Production_RegistersTheNullRunner_WhichStartsNothing()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage health = await client.GetAsync("/health");

        IReviewRunner runner = factory.Services.GetRequiredService<IReviewRunner>();

        Assert.IsType<NullReviewRunner>(runner);
        Assert.False(runner.IsBusy);
        Assert.False(runner.TryStart(new ReviewPick("Alpha", 1, Sha, true, null)));
    }

    // --- AC-8: the loop --------------------------------------------------------------------------

    [Fact]
    public async Task Loop_WhenEnabledAtStart_PollsAtOnce_ThenEveryPollMinutes_ReadFresh()
    {
        using PrWatchFactory factory = Host(out string root, out string calls, out string claude);
        WriteSettings(factory, root, claude);
        string hash = TestFactoryRoot.Hash(root);
        using HttpClient client = factory.CreateTokenClient();
        DateTimeOffset start = factory.Clock.GetUtcNow();

        await WaitForLastPoll(client, start);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        await WaitForLastPoll(client, start.AddMinutes(5));
        using (HttpResponseMessage posted = await PostSettings(client, "{\"pollMinutes\":1}"))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForLastPoll(client, start.AddMinutes(6));

        Assert.True(FakeJobPy.Calls(calls).Count >= 3);
        Assert.Equal(hash, TestFactoryRoot.Hash(root));
    }

    [Fact]
    public async Task Loop_WhenDisabled_RunsNothing_UntilEnabledThroughThePost()
    {
        using PrWatchFactory factory = Host(out string root, out string calls, out string claude);
        string hash = TestFactoryRoot.Hash(root);
        using HttpClient client = factory.CreateTokenClient();
        using JsonDocument first = JsonDocument.Parse(await client.GetStringAsync("/api/status"));
        Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("lastPoll").ValueKind);
        Assert.False(File.Exists(calls));

        using HttpResponseMessage posted = await PostSettings(
            client, "{\"enabled\":true,\"claudePath\":" + JsonSerializer.Serialize(claude) + "}");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await WatcherRig.WaitFor(() => Task.FromResult(FakeJobPy.Calls(calls).Count >= 1), "the first poll");
        await WaitForLastPoll(client, factory.Clock.GetUtcNow());
        Assert.Equal(hash, TestFactoryRoot.Hash(root));
    }

    [Fact]
    public async Task Loop_StoppingTheHost_KillsTheRunningPoll()
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-stop-" + Guid.NewGuid().ToString("N"));
        try
        {
            int pid;
            using (PrWatchFactory factory = new(folder, ownsFolder: false))
            {
                string root = TestFactoryRoot.Create(folder);
                string calls = Path.Combine(folder, "poll-calls.jsonl");
                FakeJobPy.Write(root, calls);
                FakeJobPy.SetSleep(root, 60);
                factory.FactoryRoot = root;
                WriteSettings(factory, root, TestFactoryRoot.FakeClaude(folder));
                using HttpClient client = factory.CreateTokenClient();
                await WatcherRig.WaitFor(() => Task.FromResult(FakeJobPy.Calls(calls).Count >= 1), "the poll to start");
                pid = FakeJobPy.Calls(calls)[0].GetProperty("pid").GetInt32();
                Assert.True(WatcherRig.IsRunning(pid));
            }

            await WatcherRig.WaitFor(() => Task.FromResult(!WatcherRig.IsRunning(pid)), "the poll to be killed");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // --- AC-9: loading status.json ---------------------------------------------------------------

    [Fact]
    public async Task Load_KeepsOnlyValidRunKeys_AndAPollDropsUnknownProjects()
    {
        using WatcherRig rig = new(open: false);
        string run = "{\"headSha\":\"" + Sha + "\",\"startedAt\":null,\"endedAt\":null,\"exitCode\":0,\"result\":null}";
        string[] keys = ["Alpha-1", "factory-2", "Gamma-3", "a/b-1", "..-1", "Alpha", "Alpha-0", "Alpha-x", "-1"];
        File.WriteAllText(Path.Combine(rig.DataFolder, "status.json"),
            "{\"lastPoll\":\"2026-11-26T09:00:00.000Z\",\"runs\":{"
            + string.Join(",", keys.Select(key => JsonSerializer.Serialize(key) + ":" + run)) + "}}");
        rig.Open();
        rig.Change("{\"quietMinutes\":0}");

        Assert.Equal(["Alpha-1", "Gamma-3", "factory-2"], rig.Status.Current.Runs.Keys);
        Assert.Equal(FakeClock.DefaultNow.AddHours(-1), rig.Status.Current.LastPoll);

        await rig.Poll();

        Assert.Equal(["Alpha-1", "factory-2"], rig.Status.Current.Runs.Keys);
    }

    [Theory]
    [InlineData("prs", "[{\"project\":\"Alpha\"}]")]
    [InlineData("prs", "{}")]
    [InlineData("skipped", "[{\"project\":\"Alpha\",\"reason\":\"gh is not installed\"}]")]
    [InlineData("skipped", "[{\"project\":\"Alpha\",\"reason\":\"no_remote\",\"extra\":1}]")]
    [InlineData("pick", "{\"project\":\"Alpha\",\"number\":1,\"headSha\":\"abc\",\"at\":\"2026-11-26T09:00:00.000Z\"}")]
    [InlineData("pick", "7")]
    public void Load_TurnsInvalidPrsSkippedOrPickIntoTheEmptyValue_AndLoadsTheRest(string field, string value)
    {
        using WatcherRig rig = new(open: false);
        File.WriteAllText(Path.Combine(rig.DataFolder, "status.json"),
            "{\"lastPoll\":\"2026-11-26T09:00:00.000Z\",\"runs\":{\"Alpha-1\":{}},\"" + field + "\":" + value + "}");

        rig.Open();

        Status status = rig.Status.Current;
        Assert.Empty(status.Prs);
        Assert.Empty(status.Skipped);
        Assert.Null(status.Pick);
        Assert.Equal(FakeClock.DefaultNow.AddHours(-1), status.LastPoll);
        Assert.Equal(["Alpha-1"], status.Runs.Keys);
    }

    [Fact]
    public async Task Load_KeepsValidPrsSkippedAndPick()
    {
        using WatcherRig rig = new();
        rig.Output([WatcherRig.Pr("Alpha", 1, round: WatcherRig.Round(1, WatcherRig.ShaC, current: false))],
            [Skip("Beta", "no origin remote")]);
        await rig.Poll();
        Status before = rig.Status.Current;

        rig.Open();

        Status after = rig.Status.Current;
        Assert.Equal(before.Prs, after.Prs);
        Assert.Equal(before.Skipped, after.Skipped);
        Assert.Equal(before.Pick, after.Pick);
        Assert.NotNull(after.Pick);
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static JsonObject Set(JsonObject item, string name, JsonNode? value)
    {
        item[name] = value;
        return item;
    }

    private static JsonObject Without(JsonObject item, string name)
    {
        item.Remove(name);
        return item;
    }

    private static JsonObject Skip(string project, string reason)
    {
        return new JsonObject { ["project"] = project, ["reason"] = reason };
    }

    private static void SetRun(WatcherRig rig, string key, string head)
    {
        rig.Status.Update(status => status with
        {
            Runs = status.Runs.SetItem(key, new RunStatus { HeadSha = head, ExitCode = 0 }),
        });
    }

    /// <summary>A host on a fresh temp folder with a factory root, the fake <c>job.py</c> and a fake
    /// <c>claude</c>.</summary>
    private static PrWatchFactory Host(out string root, out string calls, out string claude)
    {
        PrWatchFactory factory = new();
        root = TestFactoryRoot.Create(factory.TempFolder);
        calls = Path.Combine(factory.TempFolder, "poll-calls.jsonl");
        FakeJobPy.Write(root, calls);
        FakeJobPy.SetOutput(root, "{\"prs\":[" + WatcherRig.Pr("Alpha", 1).ToJsonString() + "],\"skipped\":[]}");
        claude = TestFactoryRoot.FakeClaude(factory.TempFolder);
        factory.FactoryRoot = root;
        factory.TestServices = services => services.AddSingleton<IReviewRunner>(new FakeReviewRunner());
        return factory;
    }

    private static void WriteSettings(PrWatchFactory factory, string root, string claude)
    {
        JsonObject settings = new()
        {
            ["enabled"] = true,
            ["factoryRoot"] = root,
            ["claudePath"] = claude,
            ["pollMinutes"] = 5,
        };
        File.WriteAllText(Path.Combine(factory.CreateDataFolder(), SettingsStore.FileName), settings.ToJsonString());
    }

    private static Task<HttpResponseMessage> PostSettings(HttpClient client, string json)
    {
        return client.PostAsync("/api/settings", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static Task WaitForLastPoll(HttpClient client, DateTimeOffset at)
    {
        string expected = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return WatcherRig.WaitFor(async () =>
        {
            using JsonDocument status = JsonDocument.Parse(await client.GetStringAsync("/api/status"));
            JsonElement lastPoll = status.RootElement.GetProperty("lastPoll");
            return lastPoll.ValueKind == JsonValueKind.String && lastPoll.GetString() == expected;
        }, "lastPoll " + expected);
    }
}
