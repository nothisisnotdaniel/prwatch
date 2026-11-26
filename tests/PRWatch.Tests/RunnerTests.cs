using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace PRWatch.Tests;

/// <summary>The headless claude runner (backlog item 4, AC-3 to AC-10). Each test has its own temp folder, test
/// factory root, fake <c>job.py</c>, <see cref="FakeClock"/> and <see cref="FakeClaude"/>; nothing runs a real
/// <c>claude</c>, <c>gh</c> or <c>git</c>, and every wait is for a named state within
/// <see cref="WatcherRig.Bound"/>.</summary>
public sealed class RunnerTests
{
    private const string Sha = WatcherRig.ShaA;

    private const string Secret = "CLAUDE_CODE_OAUTH_TOKEN";

    // Variables that /usr/bin/python3 on macOS (the Command Line Tools shim) adds itself.
    private static readonly string[] InterpreterVariables =
        ["SDKROOT", "LC_CTYPE", "__CF_USER_TEXT_ENCODING", "CPATH", "LIBRARY_PATH", "MANPATH"];

    private static readonly ReviewPick Alpha1 = new("Alpha", 1, Sha, true, null);

    // --- AC-3: the process -----------------------------------------------------------------------

    [Theory]
    [InlineData(null, "sonnet")]
    [InlineData("cheap", "haiku")]
    public async Task Start_RunsTheResolvedClaude_WithTheArgvCwdStdinAndOnlyTheFixedEnvironment(string? mode,
        string model)
    {
        Dictionary<string, string> variables = new(StringComparer.Ordinal)
        {
            ["HOME"] = "/h-test",
            ["USER"] = "u-test",
            ["TMPDIR"] = "/t-test",
            [Secret] = "do-not-pass",
            ["PRWATCH_TEST_SECRET"] = "do-not-pass",
        };
        using Rig rig = new(readVariable: name => variables.TryGetValue(name, out string? value) ? value : null);
        if (mode is not null)
        {
            rig.Watch.Change("{\"reviewerMode\":\"" + mode + "\"}");
        }

        rig.Watch.Change("{\"maxBudgetUsd\":0.5}");
        FakeClaude.SetStream(rig.Claude, Result());

        Assert.True(rig.Runner.TryStart(Alpha1));

        await rig.WaitForEnd("Alpha-1");
        JsonElement call = Assert.Single(rig.Calls());
        string prs = Path.Combine(rig.Watch.Folder, "root-factory-prs");
        string[] argv = [.. call.GetProperty("argv").EnumerateArray().Select(item => item.GetString()!)];
        Assert.Equal(ClaudeArgs.Argv(rig.Claude, "Alpha", 1, model, 0.5, prs), argv);
        Assert.Equal(rig.Claude, argv[0]);
        Assert.Equal(model, argv[4]);
        Assert.Equal("0.5", argv[14]);
        Assert.Equal(prs, argv[18]);
        Assert.Equal(Path.GetFullPath(Real(rig.Watch.Root)), Real(call.GetProperty("cwd").GetString()!));
        Assert.Equal("", call.GetProperty("stdin").GetString());

        JsonElement env = call.GetProperty("env");
        Assert.Equal("/h-test", env.GetProperty("HOME").GetString());
        Assert.Equal("u-test", env.GetProperty("USER").GetString());
        Assert.Equal("/t-test", env.GetProperty("TMPDIR").GetString());
        Assert.Equal(prs, env.GetProperty("FACTORY_PRS_DIR").GetString());
        Assert.Equal("1", env.GetProperty("FACTORY_PR_HEADLESS").GetString());
        Assert.Equal(PollRunner.FixedPath, env.GetProperty("PATH").GetString());
        AssertOnlyTheFixedEnvironment(env);
        Assert.False(env.TryGetProperty(Secret, out _));
        Assert.False(env.TryGetProperty("PRWATCH_TEST_SECRET", out _));
        Assert.False(Directory.Exists(prs));
    }

    [Fact]
    public async Task Start_WithAReaderThatReturnsNothing_PassesNoHomeOrUser_AndTheTempPathAsTmpdir()
    {
        using Rig rig = new(readVariable: name => name == "USER" ? null : "");
        FakeClaude.SetStream(rig.Claude, Result());

        Assert.True(rig.Runner.TryStart(Alpha1));

        await rig.WaitForEnd("Alpha-1");
        JsonElement env = Assert.Single(rig.Calls()).GetProperty("env");
        Assert.False(env.TryGetProperty("HOME", out _));
        Assert.False(env.TryGetProperty("USER", out _));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), env.GetProperty("TMPDIR").GetString());
        Assert.Equal(Path.Combine(rig.Watch.Folder, "root-factory-prs"), env.GetProperty("FACTORY_PRS_DIR").GetString());
        Assert.Equal("1", env.GetProperty("FACTORY_PR_HEADLESS").GetString());
        Assert.Equal(PollRunner.FixedPath, env.GetProperty("PATH").GetString());
        AssertOnlyTheFixedEnvironment(env);
    }

    [Fact]
    public async Task Start_WithoutAReader_PassesOnTheProcessHome()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude, Result());

        Assert.True(rig.Runner.TryStart(Alpha1));

        await rig.WaitForEnd("Alpha-1");
        JsonElement env = Assert.Single(rig.Calls()).GetProperty("env");
        if (Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home)
        {
            Assert.Equal(home, env.GetProperty("HOME").GetString());
        }

        Assert.Equal(PollRunner.FixedPath, env.GetProperty("PATH").GetString());
        AssertOnlyTheFixedEnvironment(env);
    }

    /// <summary>Asserts that <paramref name="env"/> holds only the fixed allow-list and the python3 shim's own
    /// variables.</summary>
    private static void AssertOnlyTheFixedEnvironment(JsonElement env)
    {
        string[] allowed = ["HOME", "USER", "TMPDIR", "FACTORY_PRS_DIR", "FACTORY_PR_HEADLESS", "PATH"];
        foreach (JsonProperty property in env.EnumerateObject())
        {
            Assert.True(allowed.Contains(property.Name) || InterpreterVariables.Contains(property.Name),
                "passed on: " + property.Name);
        }
    }

    // --- AC-4: the claude path check -------------------------------------------------------------

    [Fact]
    public async Task Start_FollowsARepointedLink_OnlyToATargetThatPassesTheCheck()
    {
        using Rig rig = new();
        string link = Path.Combine(rig.Watch.Folder, "link", "claude");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, rig.Claude);
        rig.Watch.Change("{\"claudePath\":" + JsonSerializer.Serialize(link) + "}");
        FakeClaude.SetStream(rig.Claude, Result());

        Assert.True(rig.Runner.TryStart(Alpha1));
        await rig.WaitForEnd("Alpha-1");
        Assert.Equal(rig.Claude, Assert.Single(rig.Calls()).GetProperty("argv")[0].GetString());

        string plain = Path.Combine(rig.Watch.Folder, "plain");
        File.WriteAllText(plain, "#!/bin/sh\nexit 0\n");
        NotExecutable(plain);
        Repoint(link, plain);
        Assert.False(rig.Runner.TryStart(Alpha1 with { Number = 2 }));
        Assert.Equal(StatusError.ClaudeNotFound, rig.Watch.Status.Current.LastError?.Code);
        Assert.False(rig.Runner.IsBusy);
        Assert.Single(rig.Calls());
        Assert.False(rig.Watch.Status.Current.Runs.ContainsKey("Alpha-2"));

        string other = FakeClaude.Write(Path.Combine(rig.Watch.Folder, "other"), rig.CallsFile);
        FakeClaude.SetStream(other, Result());
        Repoint(link, other);
        Assert.True(rig.Runner.TryStart(Alpha1 with { Number = 3 }));
        Assert.Null(rig.Watch.Status.Current.LastError);
        await rig.WaitForEnd("Alpha-3");
        Assert.Equal(2, rig.Calls().Count);
        Assert.Equal(other, rig.Calls()[1].GetProperty("argv")[0].GetString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-executable")]
    [InlineData("directory")]
    [InlineData("dangling-link")]
    public void Start_WithAnUnusableClaude_StartsNothing_AndRecordsClaudeNotFound(string how)
    {
        using Rig rig = new();
        string path = rig.Claude;
        switch (how)
        {
            case "missing":
                File.Delete(path);
                break;
            case "not-executable":
                NotExecutable(path);
                break;
            case "directory":
                File.Delete(path);
                Directory.CreateDirectory(path);
                break;
            default:
                string link = Path.Combine(rig.Watch.Folder, "link");
                File.CreateSymbolicLink(link, path);
                rig.Watch.Change("{\"claudePath\":" + JsonSerializer.Serialize(link) + "}");
                File.Delete(path);
                break;
        }

        Assert.False(rig.Runner.TryStart(Alpha1));

        Assert.Equal(StatusError.ClaudeNotFound, rig.Watch.Status.Current.LastError?.Code);
        Assert.Empty(rig.Calls());
        Assert.False(rig.Runner.IsBusy);
        Assert.Empty(rig.Watch.Status.Current.Runs);
    }

    [Fact]
    public void Start_WithoutAModelForTheMode_StartsNothing_AndRecordsModelUnknown()
    {
        using Rig rig = new();
        File.WriteAllText(Path.Combine(rig.Watch.Root, "factory", "modes.json"),
            "{\"default\":\"quality\",\"modes\":{\"quality\":{\"models\":{\"builder\":\"opus\"}}}}");

        Assert.False(rig.Runner.TryStart(Alpha1));

        Assert.Equal(StatusError.ModelUnknown, rig.Watch.Status.Current.LastError?.Code);
        Assert.Empty(rig.Calls());
    }

    // --- AC-5: when a run starts -----------------------------------------------------------------

    [Fact]
    public async Task Start_WritesTheStartBeforeReturning_AndStaysBusyUntilTheEndIsWritten()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude, "#wait", Result());

        Assert.True(rig.Runner.TryStart(Alpha1));

        RunStatus started = rig.Watch.Status.Current.Runs["Alpha-1"];
        Assert.Equal(Sha, started.HeadSha);
        Assert.Equal(rig.Watch.Clock.GetUtcNow(), started.StartedAt);
        Assert.Null(started.EndedAt);
        Assert.True(rig.Runner.IsBusy);
        Assert.False(rig.Runner.TryStart(Alpha1 with { Number = 2 }));
        await WatcherRig.WaitFor(() => Task.FromResult(rig.Calls().Count == 1), "the run to start");
        Assert.True(rig.Runner.IsBusy);

        FakeClaude.Release(rig.Claude);
        await WatcherRig.WaitFor(() => Task.FromResult(!rig.Runner.IsBusy), "IsBusy is false");
        Assert.NotNull(rig.Watch.Status.Current.Runs["Alpha-1"].EndedAt);
        Assert.Single(rig.Calls());
        Assert.False(rig.Watch.Status.Current.Runs.ContainsKey("Alpha-2"));
    }

    [Theory]
    [InlineData("disabled", 1)]
    [InlineData("Gamma", 1)]
    [InlineData("../Alpha", 1)]
    [InlineData("Alpha", 0)]
    [InlineData("Alpha", 1_000_000_000)]
    [InlineData("Alpha", -5)]
    public void Start_RefusesWhileDisabled_ForAnUnknownProject_OrANumberOutOfRange(string project, int number)
    {
        using Rig rig = new();
        if (project == "disabled")
        {
            rig.Watch.Change("{\"enabled\":false}");
            project = "Alpha";
        }

        Assert.False(rig.Runner.TryStart(new ReviewPick(project, number, Sha, true, null)));

        Assert.Empty(rig.Calls());
        Assert.Empty(rig.Watch.Status.Current.Runs);
        Assert.Null(rig.Watch.Status.Current.LastError);
        Assert.False(rig.Runner.IsBusy);
    }

    // --- AC-6: reading the output ----------------------------------------------------------------

    [Fact]
    public async Task Output_TakesTheLastResult_AnyError_AndSkipsLongAndNonJsonLines()
    {
        using Rig rig = new();
        string longResult = "{\"type\":\"result\",\"is_error\":false,\"total_cost_usd\":9.9,\"pad\":\""
            + new string('x', ClaudeReviewRunner.MaxLineBytes) + "\"}";
        FakeClaude.SetStream(rig.Claude,
            "not json at all MARKER-OUTPUT",
            "[1,2]",
            Result(cost: 0.1, isError: true, text: "MARKER-OUTPUT first"),
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"MARKER-OUTPUT\"}]}}",
            Result(cost: 0.3, usage: "{\"claude-sonnet-4-5\":{\"inputTokens\":10,\"outputTokens\":20,"
                + "\"costUSD\":0.3,\"note\":\"MARKER-OUTPUT\"},\"bad name!\":{\"inputTokens\":1}}"),
            longResult);
        FakeClaude.SetStderr(rig.Claude, "MARKER-STDERR something went wrong");

        Assert.True(rig.Runner.TryStart(Alpha1));
        await rig.WaitForEnd("Alpha-1");

        RunStatus run = rig.Watch.Status.Current.Runs["Alpha-1"];
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0.3, run.CostUsd);
        Assert.True(run.IsError);
        Assert.Equal(RunStatus.Error, run.Result);
        IReadOnlyDictionary<string, double> sonnet = Assert.Single(run.ModelUsage!).Value;
        Assert.Equal("claude-sonnet-4-5", Assert.Single(run.ModelUsage!).Key);
        Assert.Equal(
            new Dictionary<string, double> { ["inputTokens"] = 10, ["outputTokens"] = 20, ["costUSD"] = 0.3 },
            sonnet);
        string file = File.ReadAllText(rig.Watch.Status.Path);
        Assert.DoesNotContain("MARKER", file, StringComparison.Ordinal);
        Assert.DoesNotContain("/factory pr", file, StringComparison.Ordinal);
        Assert.Equal(LoginStatus.Ok, rig.Watch.Status.Current.Login.State);
    }

    [Theory]
    [InlineData("ok", 0)]
    [InlineData("budget", 1)]
    [InlineData("error-exit", 1)]
    [InlineData("no-result", 0)]
    public async Task Output_GivesTheFixedResultCode(string kind, int exit)
    {
        using Rig rig = new();
        string expected;
        switch (kind)
        {
            case "ok":
                FakeClaude.SetStream(rig.Claude, Result(cost: 0.42));
                expected = RunStatus.Ok;
                break;
            case "budget":
                FakeClaude.SetStream(rig.Claude, Result(isError: true, subtype: ClaudeReviewRunner.BudgetSubtype));
                expected = RunStatus.Budget;
                break;
            case "error-exit":
                FakeClaude.SetStream(rig.Claude, Result());
                expected = RunStatus.Error;
                break;
            default:
                FakeClaude.SetStream(rig.Claude, "{\"type\":\"system\"}");
                expected = RunStatus.Error;
                break;
        }

        FakeClaude.SetExit(rig.Claude, exit);

        Assert.True(rig.Runner.TryStart(Alpha1));
        await rig.WaitForEnd("Alpha-1");

        RunStatus run = rig.Watch.Status.Current.Runs["Alpha-1"];
        Assert.Equal(expected, run.Result);
        Assert.Equal(exit, run.ExitCode);
        Assert.Equal(rig.Watch.Clock.GetUtcNow(), run.EndedAt);
        if (kind == "ok")
        {
            Assert.Equal(0.42, run.CostUsd);
            Assert.False(run.IsError);
        }

        if (kind == "no-result")
        {
            Assert.Null(run.IsError);
            Assert.Equal(LoginStatus.Unknown, rig.Watch.Status.Current.Login.State);
        }
    }

    [Fact]
    public async Task Output_Past32MiB_KillsTheRunWithItsTree_AsOutputTooLarge()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude, "#flood");

        Assert.True(rig.Runner.TryStart(Alpha1));
        await rig.WaitForEnd("Alpha-1");

        Assert.Equal(RunStatus.OutputTooLarge, rig.Watch.Status.Current.Runs["Alpha-1"].Result);
        int pid = Assert.Single(rig.Calls()).GetProperty("pid").GetInt32();
        await WatcherRig.WaitFor(() => Task.FromResult(!WatcherRig.IsRunning(pid)), "the flood to be killed");
    }

    // --- AC-7: plan usage ------------------------------------------------------------------------

    [Fact]
    public async Task RateLimitEvents_UpdatePlanUsageWhileTheRunGoes_AndEndAtTheLast()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude,
            RateLimit(0.16, 0.05, "allowed", "1764158400"),
            "#wait",
            RateLimit(0.5, 0.3, "rejected", "\"2026-11-26T15:00:00Z\""),
            Result());

        Assert.True(rig.Runner.TryStart(Alpha1));
        await WatcherRig.WaitFor(
            () => Task.FromResult(rig.Watch.Status.Current.PlanUsage?.FiveHour?.Utilization == 0.16),
            "planUsage.fiveHour.utilization is 0.16");
        PlanUsage first = rig.Watch.Status.Current.PlanUsage!;
        Assert.True(rig.Runner.IsBusy);
        Assert.False(first.Limited);
        Assert.Equal(0.05, first.SevenDay?.Utilization);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1764158400), first.FiveHour?.ResetsAt);
        Assert.Equal(rig.Watch.Clock.GetUtcNow(), first.At);

        FakeClaude.Release(rig.Claude);
        await rig.WaitForEnd("Alpha-1");

        PlanUsage last = rig.Watch.Status.Current.PlanUsage!;
        Assert.Equal(0.5, last.FiveHour?.Utilization);
        Assert.Equal(0.3, last.SevenDay?.Utilization);
        Assert.True(last.Limited);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero), last.FiveHour?.ResetsAt);
    }

    // --- AC-8: timeout and stop ------------------------------------------------------------------

    [Fact]
    public async Task Timeout_OnTheRunnersClock_KillsTheRunWithItsTree()
    {
        using Rig rig = new(TimeSpan.FromSeconds(60));
        FakeClaude.SetStream(rig.Claude, RateLimit(0.16, 0.05, "allowed", "1764158400"), "#wait", Result());

        Assert.True(rig.Runner.TryStart(Alpha1));
        await WatcherRig.WaitFor(() => Task.FromResult(rig.Watch.Status.Current.PlanUsage is not null),
            "the first event to be recorded");
        Assert.True(rig.Runner.IsBusy);
        rig.Watch.Clock.Advance(TimeSpan.FromSeconds(61));
        await rig.WaitForEnd("Alpha-1");

        Assert.Equal(RunStatus.Timeout, rig.Watch.Status.Current.Runs["Alpha-1"].Result);
        int pid = Assert.Single(rig.Calls()).GetProperty("pid").GetInt32();
        await WatcherRig.WaitFor(() => Task.FromResult(!WatcherRig.IsRunning(pid)), "the run to be killed");
    }

    [Fact]
    public async Task Stop_KillsTheRunningRun_RecordsStopped_AndStartsNothingAfter()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude, "#wait", Result());
        Assert.True(rig.Runner.TryStart(Alpha1));
        await WatcherRig.WaitFor(() => Task.FromResult(rig.Calls().Count == 1), "the run to start");

        await rig.Runner.StopAsync(CancellationToken.None);

        Assert.False(rig.Runner.IsBusy);
        Assert.Equal(RunStatus.Stopped, rig.Watch.Status.Current.Runs["Alpha-1"].Result);
        int pid = rig.Calls()[0].GetProperty("pid").GetInt32();
        await WatcherRig.WaitFor(() => Task.FromResult(!WatcherRig.IsRunning(pid)), "the run to be killed");
        Assert.False(rig.Runner.TryStart(Alpha1 with { Number = 2 }));
    }

    [Fact]
    public async Task StoppingTheApp_KillsTheRun_AndRecordsStopped()
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-run-stop-" + Guid.NewGuid().ToString("N"));
        try
        {
            string calls = Path.Combine(folder, "claude-calls.jsonl");
            string dataFolder;
            using (PrWatchFactory factory = new(folder, ownsFolder: false))
            {
                string root = TestFactoryRoot.Create(folder);
                FakeJobPy.Write(root, Path.Combine(folder, "poll-calls.jsonl"));
                FakeJobPy.SetOutput(root, "{\"prs\":[" + WatcherRig.Pr("Alpha", 1).ToJsonString() + "],\"skipped\":[]}");
                string claude = FakeClaude.Write(Path.Combine(folder, "claude-bin"), calls);
                FakeClaude.SetStream(claude, "#wait", Result());
                factory.FactoryRoot = root;
                JsonObject settings = new()
                {
                    ["enabled"] = true,
                    ["factoryRoot"] = root,
                    ["claudePath"] = claude,
                    ["quietMinutes"] = 0,
                };
                dataFolder = factory.CreateDataFolder();
                File.WriteAllText(Path.Combine(dataFolder, SettingsStore.FileName), settings.ToJsonString());
                using HttpClient client = factory.CreateTokenClient();
                await WatcherRig.WaitFor(() => Task.FromResult(FakeJobPy.Calls(calls).Count >= 1), "the run to start");
                Assert.True(factory.Services.GetRequiredService<IReviewRunner>().IsBusy);
            }

            int pid = FakeJobPy.Calls(calls)[0].GetProperty("pid").GetInt32();
            await WatcherRig.WaitFor(() => Task.FromResult(!WatcherRig.IsRunning(pid)), "the run to be killed");
            using JsonDocument status = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dataFolder, StatusStore.FileName)));
            Assert.Equal("stopped", status.RootElement.GetProperty("runs").GetProperty("Alpha-1").GetProperty("result").GetString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // --- AC-9: the login -------------------------------------------------------------------------

    [Theory]
    [InlineData("result")]
    [InlineData("stderr")]
    public async Task AuthError_SetsNeedsLogin_AndTheWatcherStartsNothingThen(string where)
    {
        using Rig rig = new();
        if (where == "result")
        {
            FakeClaude.SetStream(rig.Claude, Result(isError: true, text: "Invalid API key · Please run /login"));
        }
        else
        {
            FakeClaude.SetStream(rig.Claude, "{\"type\":\"system\"}");
            FakeClaude.SetStderr(rig.Claude, "Error: Not logged in. Run claude auth login.");
            FakeClaude.SetExit(rig.Claude, 1);
        }

        using Watcher watcher = new(rig.Watch.Settings, rig.Watch.Status,
            new PollRunner(TimeSpan.FromSeconds(60)), rig.Runner, rig.Watch.Ledger, rig.Watch.Clock);
        rig.Watch.Output([WatcherRig.Pr("Alpha", 1)]);
        await watcher.PollOnceAsync(CancellationToken.None);
        await rig.WaitForEnd("Alpha-1");

        Assert.Equal(RunStatus.Auth, rig.Watch.Status.Current.Runs["Alpha-1"].Result);
        Assert.Equal(LoginStatus.NeedsLogin, rig.Watch.Status.Current.Login.State);

        rig.Watch.Output([WatcherRig.Pr("Alpha", 1), WatcherRig.Pr("Alpha", 2)]);
        await watcher.PollOnceAsync(CancellationToken.None);
        rig.Watch.Clock.Advance(TimeSpan.FromHours(2));
        await watcher.PollOnceAsync(CancellationToken.None);

        Assert.Single(rig.Calls());
        Assert.Null(rig.Watch.Status.Current.Pick);
        Assert.False(rig.Runner.IsBusy);
    }

    [Fact]
    public async Task Stderr_WithoutAnAuthText_OrWithAResult_IsNoAuthError()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude, Result());
        FakeClaude.SetStderr(rig.Claude, "warning: please /login soon");

        Assert.True(rig.Runner.TryStart(Alpha1));
        await rig.WaitForEnd("Alpha-1");

        Assert.Equal(RunStatus.Ok, rig.Watch.Status.Current.Runs["Alpha-1"].Result);
        Assert.Equal(LoginStatus.Ok, rig.Watch.Status.Current.Login.State);
    }

    [Fact]
    public async Task PostSettings_ResetsNeedsLoginToUnknown()
    {
        using PrWatchFactory factory = new();
        factory.TestServices = services => services.AddSingleton<IReviewRunner>(new FakeReviewRunner());
        using HttpClient client = factory.CreateTokenClient();
        StatusStore store = factory.Services.GetRequiredService<StatusStore>();
        store.Update(status => status with { Login = new LoginStatus { State = LoginStatus.NeedsLogin } });

        using (HttpResponseMessage bad = await client.PostAsync("/api/settings",
            new StringContent("{\"pollMinutes\":0}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        Assert.Equal(LoginStatus.NeedsLogin, store.Current.Login.State);
        using HttpResponseMessage posted = await client.PostAsync("/api/settings",
            new StringContent("{\"pollMinutes\":5}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        using JsonDocument status = JsonDocument.Parse(await client.GetStringAsync("/api/status"));
        Assert.Equal("unknown", status.RootElement.GetProperty("login").GetProperty("state").GetString());
    }

    // --- AC-10: the credential's expiry ----------------------------------------------------------

    [Theory]
    [InlineData("iso")]
    [InlineData("seconds")]
    [InlineData("milliseconds")]
    public async Task CredentialExpiresAt_IsStored_AndExpiresSoonFollowsTheClock(string form)
    {
        using Rig rig = new();
        DateTimeOffset expires = FakeClock.DefaultNow.AddDays(8);
        string value = form switch
        {
            "iso" => "\"2026-12-04T10:00:00.000Z\"",
            "seconds" => expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            _ => expires.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        };
        FakeClaude.SetStream(rig.Claude, Result(extra: ",\"credential_expires_at\":" + value));

        Assert.True(rig.Runner.TryStart(Alpha1));
        await rig.WaitForEnd("Alpha-1");

        Assert.Equal(expires, rig.Watch.Status.Current.Login.CredentialExpiresAt);
        Assert.False(rig.Watch.Status.Current.Login.ExpiresSoon);

        rig.Watch.Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1));
        await rig.Watch.Poll();
        Assert.False(rig.Watch.Status.Current.Login.ExpiresSoon);

        rig.Watch.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Watch.Poll();
        Assert.True(rig.Watch.Status.Current.Login.ExpiresSoon);
        Assert.Equal(expires, rig.Watch.Status.Current.Login.CredentialExpiresAt);
    }

    [Fact]
    public async Task CredentialExpiresAt_WithinSevenDaysAtRunEnd_SetsExpiresSoon()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude, Result(extra: ",\"credential_expires_at\":\"2026-11-30T10:00:00Z\""));

        Assert.True(rig.Runner.TryStart(Alpha1));
        await rig.WaitForEnd("Alpha-1");

        Assert.True(rig.Watch.Status.Current.Login.ExpiresSoon);
    }

    // --- token brake (item 5, AC-7, AC-8) -------------------------------------------------------

    [Fact]
    public void Start_WhileTheBrakeHolds_StartsNoClaude_AndWritesNoLedgerEntry()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude, Result());
        DateTimeOffset now = rig.Watch.Clock.GetUtcNow();
        rig.Watch.Ledger.Start(now.AddMinutes(-30));
        rig.Watch.Ledger.Start(now.AddMinutes(-20));
        rig.Watch.Ledger.Start(now.AddMinutes(-10));

        Assert.False(rig.Runner.TryStart(Alpha1));

        Assert.False(rig.Runner.IsBusy);
        Assert.Empty(rig.Calls());
        Assert.Equal(3, rig.Watch.Ledger.Entries.Count);
        Assert.False(rig.Watch.Status.Current.Runs.ContainsKey("Alpha-1"));
        Assert.Equal(
            new BrakeStatus { Holding = true, Reason = TokenBrake.RunsPerHour, Until = now.AddMinutes(30) },
            rig.Watch.Status.Current.Brake);
    }

    [Fact]
    public async Task End_TakesTheLastResultsCostIntoTheLedger_AndARateLimitLeavesTheBrakeHolding()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude,
            Result(cost: 0.25),
            RateLimit(0.5, 0.3, "rejected", "\"2026-11-26T15:00:00Z\""),
            Result(cost: 0.6));

        Assert.True(rig.Runner.TryStart(Alpha1));
        await rig.WaitForEnd("Alpha-1");

        LedgerEntry entry = Assert.Single(rig.Watch.Ledger.Entries);
        Assert.Equal(rig.Watch.Clock.GetUtcNow(), entry.StartedAt);
        Assert.Equal(rig.Watch.Clock.GetUtcNow(), entry.EndedAt);
        Assert.Equal(0.6, entry.CostUsd);
        Assert.Equal(entry, Assert.Single(new UsageLedger(rig.Watch.DataFolder, rig.Watch.Status, rig.Watch.Clock).Entries));
        Assert.Equal(
            new BrakeStatus
            {
                Holding = true,
                Reason = TokenBrake.RateLimited,
                Until = new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero),
            },
            rig.Watch.Status.Current.Brake);
        Assert.False(rig.Runner.TryStart(Alpha1 with { Number = 2 }));
        Assert.Single(rig.Calls());
    }

    [Fact]
    public async Task Start_AddsTheLedgerEntryWhileTheRunGoes_AndARunWithoutAResultKeepsNoCost()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude, "{\"type\":\"system\"}", "#wait");
        DateTimeOffset startedAt = rig.Watch.Clock.GetUtcNow();

        Assert.True(rig.Runner.TryStart(Alpha1));

        LedgerEntry started = Assert.Single(rig.Watch.Ledger.Entries);
        Assert.Equal(new LedgerEntry { StartedAt = startedAt }, started);
        Assert.True(rig.Runner.IsBusy);
        rig.Watch.Clock.Advance(TimeSpan.FromMinutes(3));
        FakeClaude.Release(rig.Claude);
        await rig.WaitForEnd("Alpha-1");

        Assert.Equal(new LedgerEntry { StartedAt = startedAt, EndedAt = startedAt.AddMinutes(3), CostUsd = null },
            Assert.Single(rig.Watch.Ledger.Entries));
        Assert.Equal(new BrakeStatus(), rig.Watch.Status.Current.Brake);
    }

    // --- job 428: the update lock -----------------------------------------------------------------

    [Fact]
    public async Task UpdateLock_IsHeldWithTheMarkerWhileTheRunGoes_AndGoneWhenItEnds()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude, "#wait", Result());

        Assert.True(rig.Runner.TryStart(Alpha1));

        Assert.True(Directory.Exists(rig.Lock.Path));
        Assert.True(File.Exists(rig.Lock.MarkerPath));
        Assert.Equal(UpdateLock.Run, rig.Watch.Status.Current.UpdateLock);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(rig.Lock.Path));
        }

        FakeClaude.Release(rig.Claude);
        await rig.WaitForEnd("Alpha-1");

        Assert.False(Directory.Exists(rig.Lock.Path));
        Assert.Null(rig.Watch.Status.Current.UpdateLock);
    }

    [Fact]
    public async Task UpdateLock_HeldByTheUpdater_StartsNothing_LeavesItAlone_AndTheNextPickRunsOnceItIsGone()
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude, Result());
        Directory.CreateDirectory(rig.Lock.Path);
        string inside = Path.Combine(rig.Lock.Path, "updater-file");
        File.WriteAllText(inside, "keep");
        DateTime old = new(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(rig.Lock.Path, old);

        Assert.False(rig.Runner.TryStart(Alpha1));

        Assert.Empty(rig.Calls());
        Assert.False(rig.Runner.IsBusy);
        Assert.Equal(UpdateLock.Updater, rig.Watch.Status.Current.UpdateLock);
        Assert.False(rig.Watch.Status.Current.Runs.ContainsKey("Alpha-1"));
        Assert.Empty(rig.Watch.Ledger.Entries);
        Assert.Equal(["updater-file"], Directory.GetFileSystemEntries(rig.Lock.Path).Select(Path.GetFileName));
        Assert.Equal("keep", File.ReadAllText(inside));
        Assert.Equal(old, Directory.GetLastWriteTimeUtc(rig.Lock.Path));

        Directory.Delete(rig.Lock.Path, recursive: true);
        Assert.True(rig.Runner.TryStart(Alpha1));
        await rig.WaitForEnd("Alpha-1");

        Assert.Single(rig.Calls());
        Assert.Null(rig.Watch.Status.Current.UpdateLock);
        Assert.False(Directory.Exists(rig.Lock.Path));
    }

    [Fact]
    public async Task UpdateLock_IsTouchedEveryTenMinutesOfTheRunnersClock()
    {
        // A run timeout past the 80 min the clock moves, so the run is still going at every check.
        using Rig rig = new(TimeSpan.FromHours(24));
        FakeClaude.SetStream(rig.Claude, "#wait", Result());
        Assert.True(rig.Runner.TryStart(Alpha1));
        DateTime old = new(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(rig.Lock.Path, old);

        rig.Watch.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(old, Directory.GetLastWriteTimeUtc(rig.Lock.Path));
        rig.Watch.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(rig.Watch.Clock.GetUtcNow().UtcDateTime, Directory.GetLastWriteTimeUtc(rig.Lock.Path));
        for (int round = 0; round < 7; round++)
        {
            rig.Watch.Clock.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(rig.Watch.Clock.GetUtcNow().UtcDateTime, Directory.GetLastWriteTimeUtc(rig.Lock.Path));
        }

        Assert.True(rig.Runner.IsBusy);
        FakeClaude.Release(rig.Claude);
        await rig.WaitForEnd("Alpha-1");
        Assert.False(Directory.Exists(rig.Lock.Path));
    }

    [Theory]
    [InlineData("link-to-a-folder-with-the-marker")]
    [InlineData("dangling-link")]
    [InlineData("file")]
    public void UpdateLock_ThatIsASymlinkOrAFile_CountsAsTheUpdater_IsNeverFollowedOrDeleted_AndStartsNoRun(
        string how)
    {
        using Rig rig = new();
        FakeClaude.SetStream(rig.Claude, Result());
        string target = Path.Combine(rig.Watch.Folder, "elsewhere");
        string targetMarker = Path.Combine(target, UpdateLock.MarkerName);
        switch (how)
        {
            case "link-to-a-folder-with-the-marker":
                Directory.CreateDirectory(target);
                File.WriteAllText(targetMarker, "");
                Directory.CreateSymbolicLink(rig.Lock.Path, target);
                break;
            case "dangling-link":
                Directory.CreateSymbolicLink(rig.Lock.Path, target);
                break;
            default:
                File.WriteAllText(rig.Lock.Path, "a file");
                break;
        }

        Assert.Equal(UpdateLock.Updater, rig.Lock.Holder());
        Assert.False(rig.Lock.Release());
        Assert.False(rig.Runner.TryStart(Alpha1));

        Assert.Empty(rig.Calls());
        Assert.Equal(UpdateLock.Updater, rig.Watch.Status.Current.UpdateLock);
        if (how == "file")
        {
            Assert.Equal("a file", File.ReadAllText(rig.Lock.Path));
        }
        else
        {
            Assert.Equal(target, new FileInfo(rig.Lock.Path).LinkTarget);
        }

        if (how == "link-to-a-folder-with-the-marker")
        {
            Assert.True(File.Exists(targetMarker));
            Assert.Equal([UpdateLock.MarkerName], Directory.GetFileSystemEntries(target).Select(Path.GetFileName));
        }
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static string Result(double cost = 0.25, bool isError = false, string text = "done",
        string? subtype = null, string usage = "{\"claude-sonnet-4-5\":{\"inputTokens\":1}}", string extra = "")
    {
        return "{\"type\":\"result\",\"subtype\":" + JsonSerializer.Serialize(subtype ?? (isError ? "error" : "success"))
            + ",\"is_error\":" + (isError ? "true" : "false")
            + ",\"result\":" + JsonSerializer.Serialize(text)
            + ",\"total_cost_usd\":" + cost.ToString(CultureInfo.InvariantCulture)
            + ",\"modelUsage\":" + usage + extra + "}";
    }

    private static string RateLimit(double five, double seven, string state, string resetsAt)
    {
        string Window(double utilization)
        {
            return "{\"utilization\":" + utilization.ToString(CultureInfo.InvariantCulture)
                + ",\"resetsAt\":" + resetsAt + "}";
        }

        return "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"" + state + "\",\"unifiedWindows\":{"
            + "\"five_hour\":" + Window(five) + ",\"seven_day\":" + Window(seven) + "}}}";
    }

    private static void NotExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void Repoint(string link, string target)
    {
        string next = link + ".next";
        File.CreateSymbolicLink(next, target);
        File.Move(next, link, overwrite: true);
    }

    private static string Real(string path)
    {
        string full = Path.GetFullPath(path);
        return full.StartsWith("/private/var/", StringComparison.Ordinal) ? full["/private".Length..] : full;
    }

    /// <summary>A <see cref="WatcherRig"/> with <c>enabled</c> on, a <see cref="FakeClaude"/> (in
    /// <c>&lt;folder&gt;/claude-bin</c>, calls to <c>&lt;folder&gt;/claude-calls.jsonl</c>) as
    /// <c>claudePath</c>, and a <see cref="ClaudeReviewRunner"/> on the rig's stores, clock and an
    /// <see cref="UpdateLock"/> in the data folder.</summary>
    private sealed class Rig : IDisposable
    {
        /// <param name="readVariable">The runner's environment reader (<see cref="ClaudeReviewRunner.ReadVariable"/>);
        /// <c>null</c> keeps the real process environment.</param>
        public Rig(TimeSpan? timeout = null, Func<string, string?>? readVariable = null)
        {
            Watch = new WatcherRig();
            CallsFile = Path.Combine(Watch.Folder, "claude-calls.jsonl");
            Claude = FakeClaude.Write(Path.Combine(Watch.Folder, "claude-bin"), CallsFile);
            Watch.Change("{\"enabled\":true,\"claudePath\":" + JsonSerializer.Serialize(Claude) + "}");
            Lock = new UpdateLock(Watch.DataFolder);
            Runner = new ClaudeReviewRunner(Watch.Settings, Watch.Status, Watch.Ledger, Watch.Clock, Lock,
                timeout ?? TimeSpan.FromSeconds(ClaudeReviewRunner.DefaultTimeoutSeconds))
            {
                ReadVariable = readVariable ?? Environment.GetEnvironmentVariable,
            };
        }

        public WatcherRig Watch { get; }

        /// <summary>The update lock in the rig's data folder.</summary>
        public UpdateLock Lock { get; }

        public string CallsFile { get; }

        public string Claude { get; }

        public ClaudeReviewRunner Runner { get; }

        public IReadOnlyList<JsonElement> Calls()
        {
            return FakeJobPy.Calls(CallsFile);
        }

        /// <summary>Waits until <c>runs[key].endedAt</c> is set and <c>IsBusy</c> is false.</summary>
        public Task WaitForEnd(string key)
        {
            return WatcherRig.WaitFor(() => Task.FromResult(
                Watch.Status.Current.Runs.TryGetValue(key, out RunStatus? run)
                && run.EndedAt is not null
                && !Runner.IsBusy), "runs[" + key + "].endedAt is set");
        }

        public void Dispose()
        {
            Runner.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            Runner.Dispose();
            Watch.Dispose();
        }
    }
}
