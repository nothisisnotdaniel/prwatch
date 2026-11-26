using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PRWatch.Tests;

/// <summary>The update lock as the hosted app holds it (job 428, AC-2 and AC-5): <c>&lt;data&gt;/.update.lock</c>
/// with the marker <c>prwatch-run</c> for the whole of every run, shown as <c>updateLock</c> in
/// <c>/api/status</c>, and removed at app start only when PRWatch left it. Each test has its own temp folder,
/// factory root, fake <c>job.py</c>, <see cref="FakeClaude"/> and <see cref="FakeClock"/>; every wait is for a
/// named state within <see cref="WatcherRig.Bound"/>.</summary>
public sealed class UpdateLockTests
{
    private const string Result =
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"done\",\"total_cost_usd\":0.1}";

    // --- AC-2: held for the whole run, for every result ----------------------------------------

    [Theory]
    [InlineData(RunStatus.Ok)]
    [InlineData(RunStatus.Error)]
    [InlineData(RunStatus.Timeout)]
    [InlineData(RunStatus.Stopped)]
    public async Task Run_HoldsTheLockWithTheMarker_ShownAsRun_AndEndsWithoutIt(string result)
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            string dataFolder;
            using (PrWatchFactory factory = new(folder, ownsFolder: false))
            {
                string claude = Arrange(factory, folder);
                FakeClaude.SetStream(claude, "#wait", Result);
                if (result == RunStatus.Error)
                {
                    FakeClaude.SetExit(claude, 1);
                }

                if (result == RunStatus.Timeout)
                {
                    factory.Settings["RunTimeoutSeconds"] = "60";
                }

                dataFolder = factory.DataFolder;
                UpdateLock updateLock = new(dataFolder);
                using HttpClient client = factory.CreateTokenClient();
                await WaitForStatus(client, status => Run(status)?["startedAt"] is not null, "runs.Alpha-1.startedAt");
                await WatcherRig.WaitFor(() => Task.FromResult(FakeJobPy.Calls(Path.Combine(folder, "claude-calls.jsonl")).Count == 1),
                    "the fake claude to run");

                JsonObject running = await Status(client);
                Assert.Equal(UpdateLock.Run, (string?)running["updateLock"]);
                Assert.Null(Run(running)!["endedAt"]);
                Assert.True(Directory.Exists(updateLock.Path));
                Assert.True(File.Exists(updateLock.MarkerPath));

                if (result == RunStatus.Stopped)
                {
                    // The app stops mid-run: disposing the host below kills the run.
                }
                else
                {
                    if (result == RunStatus.Timeout)
                    {
                        factory.Clock.Advance(TimeSpan.FromSeconds(61));
                    }
                    else
                    {
                        FakeClaude.Release(claude);
                    }

                    JsonObject ended = await WaitForStatus(client, status => Run(status)?["endedAt"] is not null,
                        "runs.Alpha-1.endedAt");
                    Assert.Equal(result, (string?)Run(ended)!["result"]);
                    Assert.Null(ended["updateLock"]);
                    Assert.False(Directory.Exists(updateLock.Path));
                }
            }

            if (result == RunStatus.Stopped)
            {
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dataFolder, StatusStore.FileName)));
                JsonElement run = saved.RootElement.GetProperty("runs").GetProperty("Alpha-1");
                Assert.Equal(RunStatus.Stopped, run.GetProperty("result").GetString());
                Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("updateLock").ValueKind);
                Assert.False(Directory.Exists(Path.Combine(dataFolder, UpdateLock.FolderName)));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // --- AC-5: app start -----------------------------------------------------------------------

    [Fact]
    public async Task AppStart_RemovesALockPRWatchLeftBehind()
    {
        using PrWatchFactory factory = new();
        UpdateLock updateLock = new(factory.CreateDataFolder());
        Directory.CreateDirectory(updateLock.Path);
        File.WriteAllText(updateLock.MarkerPath, "");

        using HttpClient client = factory.CreateTokenClient();

        Assert.False(Directory.Exists(updateLock.Path));
        Assert.Null((await Status(client))["updateLock"]);
    }

    // --- job 461: the marker names its owner -------------------------------------------------------

    [Fact]
    public void TryAcquire_WritesThePidAndStartTicksOfThisProcess_InAPrivateFolder()
    {
        string folder = NewFolder();
        try
        {
            UpdateLock updateLock = new(folder);

            Assert.True(updateLock.TryAcquire());

            long ticks;
            using (Process own = Process.GetCurrentProcess())
            {
                ticks = own.StartTime.ToUniversalTime().Ticks;
            }

            Assert.Equal(Encoding.ASCII.GetBytes(Environment.ProcessId + " " + ticks + "\n"),
                File.ReadAllBytes(updateLock.MarkerPath));
            Assert.Equal(UpdateLock.Run, updateLock.Holder());
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(updateLock.Path));
            }

            Assert.True(updateLock.Release());
            Assert.False(Directory.Exists(updateLock.Path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AppStart_WithALockOfALiveOtherProcess_LeavesIt_ShowsRun_AndStartsNoRun()
    {
        string folder = NewFolder();
        using Process sleeper = StartSleeper();
        try
        {
            byte[] marker = UpdateLock.Marker(sleeper.Id, UpdateLock.StartTicks(sleeper));
            using PrWatchFactory factory = new(folder, ownsFolder: false);
            string claude = Arrange(factory, folder);
            FakeClaude.SetStream(claude, Result);
            UpdateLock updateLock = new(factory.DataFolder);
            Directory.CreateDirectory(updateLock.Path);
            File.WriteAllBytes(updateLock.MarkerPath, marker);
            DateTime folderTime = Directory.GetLastWriteTimeUtc(updateLock.Path);

            using HttpClient client = factory.CreateTokenClient();
            string polls = Path.Combine(folder, "poll-calls.jsonl");
            await WaitForStatus(client, status => status["pick"] is not null, "the first pick");
            await WatcherRig.WaitFor(() => Task.FromResult(FakeJobPy.Calls(polls).Count >= 1), "the first poll");

            JsonObject status = await Status(client);
            Assert.Equal(UpdateLock.Run, (string?)status["updateLock"]);
            Assert.Null(Run(status));
            Assert.Empty(FakeJobPy.Calls(Path.Combine(folder, "claude-calls.jsonl")));
            Assert.Equal(marker, File.ReadAllBytes(updateLock.MarkerPath));
            Assert.Equal([UpdateLock.MarkerName], Directory.GetFileSystemEntries(updateLock.Path).Select(Path.GetFileName));
            Assert.Equal(folderTime, Directory.GetLastWriteTimeUtc(updateLock.Path));
        }
        finally
        {
            StopSleeper(sleeper);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("exited")]
    [InlineData("reused-pid")]
    [InlineData("empty")]
    [InlineData("longer-than-64")]
    [InlineData("abc")]
    [InlineData("-1 5")]
    [InlineData("12")]
    [InlineData("0 5")]
    [InlineData("12 0")]
    [InlineData("12 5 7")]
    [InlineData(" 12 5")]
    public async Task AppStart_WithAMarkerWhoseOwnerIsGoneOrUnreadable_RemovesTheLock(string how)
    {
        Process? sleeper = null;
        try
        {
            byte[] marker;
            switch (how)
            {
                case "exited":
                    marker = await ExitedMarker();
                    break;
                case "reused-pid":
                    sleeper = StartSleeper();
                    marker = UpdateLock.Marker(sleeper.Id, UpdateLock.StartTicks(sleeper) + 1);
                    break;
                case "empty":
                    marker = [];
                    break;
                case "longer-than-64":
                    marker = Encoding.ASCII.GetBytes(new string('1', 40) + " " + new string('2', 40) + "\n");
                    break;
                default:
                    marker = Encoding.ASCII.GetBytes(how);
                    break;
            }

            using PrWatchFactory factory = new();
            UpdateLock updateLock = new(factory.CreateDataFolder());
            Directory.CreateDirectory(updateLock.Path);
            File.WriteAllBytes(updateLock.MarkerPath, marker);

            using HttpClient client = factory.CreateTokenClient();

            Assert.Null((await Status(client))["updateLock"]);
            Assert.False(Directory.Exists(updateLock.Path));
        }
        finally
        {
            if (sleeper is not null)
            {
                StopSleeper(sleeper);
                sleeper.Dispose();
            }
        }
    }

    [Fact]
    public void Release_OfALockOfALiveOtherProcess_ReturnsFalseAndLeavesIt_OfItsOwnLock_RemovesIt()
    {
        string folder = NewFolder();
        using Process sleeper = StartSleeper();
        try
        {
            UpdateLock updateLock = new(folder);
            byte[] marker = UpdateLock.Marker(sleeper.Id, UpdateLock.StartTicks(sleeper));
            Directory.CreateDirectory(updateLock.Path);
            File.WriteAllBytes(updateLock.MarkerPath, marker);

            Assert.False(updateLock.Release());
            Assert.False(updateLock.ReleaseLeftBehind());
            Assert.Equal(marker, File.ReadAllBytes(updateLock.MarkerPath));
            Assert.Equal(UpdateLock.Run, updateLock.Holder());

            File.Delete(updateLock.MarkerPath);
            Directory.Delete(updateLock.Path);
            Assert.True(updateLock.TryAcquire());
            Assert.True(updateLock.Release());
            Assert.False(Directory.Exists(updateLock.Path));
            Assert.Null(updateLock.Holder());
        }
        finally
        {
            StopSleeper(sleeper);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Release_OfALockWithAnUnreadableOrDeadOwner_ReturnsFalse()
    {
        string folder = NewFolder();
        try
        {
            UpdateLock updateLock = new(folder);
            Directory.CreateDirectory(updateLock.Path);
            byte[] marker = await ExitedMarker();
            File.WriteAllBytes(updateLock.MarkerPath, marker);

            Assert.False(updateLock.Release());
            Assert.Equal(marker, File.ReadAllBytes(updateLock.MarkerPath));

            File.WriteAllText(updateLock.MarkerPath, "");
            Assert.False(updateLock.Release());
            Assert.True(File.Exists(updateLock.MarkerPath));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AMarkerThatIsASymlink_IsNeverFollowedOrDeleted_ByReleaseOrReleaseLeftBehind()
    {
        string folder = NewFolder();
        try
        {
            UpdateLock updateLock = new(folder);
            Directory.CreateDirectory(updateLock.Path);
            string target = Path.Combine(folder, "elsewhere-marker");
            byte[] marker = await ExitedMarker();
            File.WriteAllBytes(target, marker);
            File.CreateSymbolicLink(updateLock.MarkerPath, target);

            Assert.Equal(UpdateLock.Updater, updateLock.Holder());
            Assert.False(updateLock.ReleaseLeftBehind());
            Assert.False(updateLock.Release());

            Assert.Equal(target, new FileInfo(updateLock.MarkerPath).LinkTarget);
            Assert.Equal(marker, File.ReadAllBytes(target));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("1 2\n", 1, 2L)]
    [InlineData("1 2", 1, 2L)]
    [InlineData("123 638000000000000000\n", 123, 638000000000000000L)]
    public void ParseOwner_ReadsTwoPositiveIntegers(string text, int pid, long ticks)
    {
        Assert.Equal(new UpdateLock.Owner(pid, ticks), UpdateLock.ParseOwner(Encoding.ASCII.GetBytes(text)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("abc")]
    [InlineData("-1 5")]
    [InlineData("12")]
    [InlineData("12 ")]
    [InlineData(" 12")]
    [InlineData("1  2")]
    [InlineData("1 2\n\n")]
    [InlineData("1 2 3")]
    [InlineData("+1 2")]
    [InlineData("1\t2")]
    [InlineData("99999999999 2")]
    [InlineData("1 99999999999999999999")]
    public void ParseOwner_RejectsAnythingElse(string text)
    {
        Assert.Null(UpdateLock.ParseOwner(Encoding.ASCII.GetBytes(text)));
    }

    [Theory]
    [InlineData("folder-without-marker")]
    [InlineData("link-to-a-folder-with-the-marker")]
    [InlineData("marker-that-is-a-link-to-a-dead-owner")]
    [InlineData("file")]
    public async Task AppStart_LeavesALockThatIsNotPRWatchs_AndShowsItAsUpdater(string how)
    {
        using PrWatchFactory factory = new();
        UpdateLock updateLock = new(factory.CreateDataFolder());
        string target = Path.Combine(factory.TempFolder, "elsewhere");
        switch (how)
        {
            case "folder-without-marker":
                Directory.CreateDirectory(updateLock.Path);
                File.WriteAllText(Path.Combine(updateLock.Path, "updater-file"), "keep");
                break;
            case "link-to-a-folder-with-the-marker":
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, UpdateLock.MarkerName), "");
                Directory.CreateSymbolicLink(updateLock.Path, target);
                break;
            case "marker-that-is-a-link-to-a-dead-owner":
                Directory.CreateDirectory(updateLock.Path);
                File.WriteAllBytes(target, await ExitedMarker());
                File.CreateSymbolicLink(updateLock.MarkerPath, target);
                break;
            default:
                File.WriteAllText(updateLock.Path, "a file");
                break;
        }

        byte[] targetBytes = File.Exists(target) ? File.ReadAllBytes(target) : [];
        using HttpClient client = factory.CreateTokenClient();

        Assert.Equal(UpdateLock.Updater, (string?)(await Status(client))["updateLock"]);
        switch (how)
        {
            case "folder-without-marker":
                Assert.Equal("keep", File.ReadAllText(Path.Combine(updateLock.Path, "updater-file")));
                break;
            case "link-to-a-folder-with-the-marker":
                Assert.Equal(target, new FileInfo(updateLock.Path).LinkTarget);
                Assert.True(File.Exists(Path.Combine(target, UpdateLock.MarkerName)));
                break;
            case "marker-that-is-a-link-to-a-dead-owner":
                Assert.Equal(target, new FileInfo(updateLock.MarkerPath).LinkTarget);
                Assert.Equal(targetBytes, File.ReadAllBytes(target));
                break;
            default:
                Assert.Equal("a file", File.ReadAllText(updateLock.Path));
                break;
        }
    }

    [Fact]
    public async Task AppStart_WithTheUpdatersLock_StartsNoRun()
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            using PrWatchFactory factory = new(folder, ownsFolder: false);
            string claude = Arrange(factory, folder);
            FakeClaude.SetStream(claude, Result);
            UpdateLock updateLock = new(factory.DataFolder);
            Directory.CreateDirectory(updateLock.Path);

            using HttpClient client = factory.CreateTokenClient();
            string polls = Path.Combine(folder, "poll-calls.jsonl");
            await WaitForStatus(client, status => status["pick"] is not null, "the first pick");
            await WatcherRig.WaitFor(() => Task.FromResult(FakeJobPy.Calls(polls).Count >= 1), "the first poll");

            JsonObject status = await Status(client);
            Assert.Equal(UpdateLock.Updater, (string?)status["updateLock"]);
            Assert.Null(Run(status));
            Assert.Empty(FakeJobPy.Calls(Path.Combine(folder, "claude-calls.jsonl")));
            Assert.True(Directory.Exists(updateLock.Path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A live owner: a child <c>/bin/sleep 600</c>, stopped by <see cref="StopSleeper"/> in the test's
    /// <c>finally</c>.</summary>
    private static Process StartSleeper()
    {
        ProcessStartInfo info = new("/bin/sleep") { UseShellExecute = false };
        info.ArgumentList.Add("600");
        return Process.Start(info)!;
    }

    private static void StopSleeper(Process sleeper)
    {
        if (!sleeper.HasExited)
        {
            sleeper.Kill();
        }

        sleeper.WaitForExit();
    }

    /// <summary>The marker of a process that has exited: a child <c>/usr/bin/true</c>, awaited before its PID is
    /// written.</summary>
    private static async Task<byte[]> ExitedMarker()
    {
        ProcessStartInfo info = new("/usr/bin/true") { UseShellExecute = false };
        using Process process = Process.Start(info)!;
        await process.WaitForExitAsync();

        // Its start time can't be read once it has exited; any positive ticks name it.
        return UpdateLock.Marker(process.Id, 638000000000000000L);
    }

    /// <summary>A factory root with one open PR <c>Alpha#1</c>, a fake <c>claude</c> and the settings that let the
    /// watcher pick it at once; returns the fake's path.</summary>
    private static string Arrange(PrWatchFactory factory, string folder)
    {
        string root = TestFactoryRoot.Create(folder);
        FakeJobPy.Write(root, Path.Combine(folder, "poll-calls.jsonl"));
        FakeJobPy.SetOutput(root, "{\"prs\":[" + WatcherRig.Pr("Alpha", 1).ToJsonString() + "],\"skipped\":[]}");
        string claude = FakeClaude.Write(Path.Combine(folder, "claude-bin"), Path.Combine(folder, "claude-calls.jsonl"));
        factory.FactoryRoot = root;
        JsonObject settings = new()
        {
            ["enabled"] = true,
            ["factoryRoot"] = root,
            ["claudePath"] = claude,
            ["quietMinutes"] = 0,
        };
        string dataFolder = factory.CreateDataFolder();
        File.WriteAllText(Path.Combine(dataFolder, SettingsStore.FileName), settings.ToJsonString());
        return claude;
    }

    private static JsonObject? Run(JsonObject status)
    {
        return status["runs"]?["Alpha-1"] as JsonObject;
    }

    private static async Task<JsonObject> Status(HttpClient client)
    {
        string body = await client.GetStringAsync(PrWatchApp.StatusRoute);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task<JsonObject> WaitForStatus(HttpClient client, Func<JsonObject, bool> condition, string what)
    {
        JsonObject? last = null;
        await WatcherRig.WaitFor(async () =>
        {
            last = await Status(client);
            return condition(last);
        }, what);
        return last!;
    }
}
