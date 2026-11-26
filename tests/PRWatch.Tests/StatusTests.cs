using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace PRWatch.Tests;

/// <summary><c>GET /api/status</c> and <c>status.json</c>: the initial state, updates through
/// <see cref="StatusStore"/>, concurrent updates, symlinks and loading at start.</summary>
public sealed class StatusTests
{
    private const string Route = "/api/status";

    private const string Initial =
        "{\"lastPoll\":null,\"runs\":{},\"prs\":[],\"skipped\":[],\"pick\":null,"
        + "\"brake\":{\"holding\":false,\"reason\":null,\"until\":null},"
        + "\"login\":{\"state\":\"unknown\",\"credentialExpiresAt\":null,\"expiresSoon\":false},"
        + "\"planUsage\":null,\"updateLock\":null,\"lastError\":null}";

    [Fact]
    public async Task Get_OnAFreshHost_IsTheInitialStatus()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = await Start(factory);

        using HttpResponseMessage response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Initial, await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(StatusFile(factory)));
    }

    [Fact]
    public async Task Update_ShowsInTheRoute_WithUtcTimesFromTheClock()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = await Start(factory);
        StatusStore store = factory.Services.GetRequiredService<StatusStore>();
        DateTimeOffset now = factory.Clock.GetUtcNow();

        store.Update(status => status with
        {
            LastPoll = now,
            Runs = status.Runs.SetItem("Alpha-12", new RunStatus
            {
                HeadSha = "abc123",
                StartedAt = now.AddMinutes(-3).ToOffset(TimeSpan.FromHours(2)),
                EndedAt = now,
                ExitCode = 0,
                Result = "clean",
            }),
            Brake = new BrakeStatus { Holding = true, Reason = "runs_per_hour", Until = now.AddHours(1) },
            Login = new LoginStatus { State = LoginStatus.Ok, CredentialExpiresAt = now.AddDays(30) },
        });
        string body = await client.GetStringAsync(Route);

        Assert.Equal(
            "{\"lastPoll\":\"2026-11-26T10:00:00.000Z\",\"runs\":{\"Alpha-12\":{\"headSha\":\"abc123\","
            + "\"startedAt\":\"2026-11-26T09:57:00.000Z\",\"endedAt\":\"2026-11-26T10:00:00.000Z\",\"exitCode\":0,"
            + "\"result\":\"clean\",\"costUsd\":null,\"isError\":null,\"modelUsage\":null}},\"prs\":[],\"skipped\":[],\"pick\":null,\"brake\":{\"holding\":true,\"reason\":\"runs_per_hour\","
            + "\"until\":\"2026-11-26T11:00:00.000Z\"},\"login\":{\"state\":\"ok\","
            + "\"credentialExpiresAt\":\"2026-12-26T10:00:00.000Z\",\"expiresSoon\":false},\"planUsage\":null,"
            + "\"updateLock\":null,\"lastError\":null}",
            body);
        using JsonDocument file = JsonDocument.Parse(File.ReadAllBytes(StatusFile(factory)));
        Assert.Equal("2026-11-26T10:00:00.000Z", file.RootElement.GetProperty("lastPoll").GetString());
    }

    [Fact]
    public async Task FiftyParallelUpdates_LoseNothing_InMemoryOrInTheFile()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = await Start(factory);
        StatusStore store = factory.Services.GetRequiredService<StatusStore>();

        Task[] updates = [.. Enumerable.Range(1, 50).Select(number => Task.Run(() => store.Update(status => status with
        {
            Runs = status.Runs.SetItem("Alpha-" + number, new RunStatus { ExitCode = number }),
        })))];
        await Task.WhenAll(updates);

        Assert.Equal(50, store.Current.Runs.Count);
        using JsonDocument file = JsonDocument.Parse(File.ReadAllBytes(StatusFile(factory)));
        Assert.Equal(50, file.RootElement.GetProperty("runs").EnumerateObject().Count());
        using JsonDocument route = JsonDocument.Parse(await client.GetStringAsync(Route));
        Assert.Equal(50, route.RootElement.GetProperty("runs").EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Null, route.RootElement.GetProperty("lastError").ValueKind);
        Assert.Empty(Directory.EnumerateFiles(factory.DataFolder, "*.tmp"));
    }

    [Fact]
    public async Task SymlinkedStatusFile_IsNotWrittenThrough_AndTheStatusKeepsWorkingInMemory()
    {
        using PrWatchFactory factory = new();
        string outside = Path.Combine(factory.TempFolder, "outside.json");
        File.WriteAllText(outside, "keep me\n");
        byte[] before = File.ReadAllBytes(outside);
        File.CreateSymbolicLink(Path.Combine(factory.CreateDataFolder(), "status.json"), outside);
        using HttpClient client = await Start(factory);
        StatusStore store = factory.Services.GetRequiredService<StatusStore>();

        store.Update(status => status with { LastPoll = factory.Clock.GetUtcNow() });
        using JsonDocument body = JsonDocument.Parse(await client.GetStringAsync(Route));

        Assert.Equal(before, File.ReadAllBytes(outside));
        Assert.Equal("2026-11-26T10:00:00.000Z", body.RootElement.GetProperty("lastPoll").GetString());
        JsonElement error = body.RootElement.GetProperty("lastError");
        Assert.Equal("status_write_failed", error.GetProperty("code").GetString());
        Assert.Equal("2026-11-26T10:00:00.000Z", error.GetProperty("at").GetString());
    }

    [Fact]
    public async Task ExistingValidFile_IsLoadedAtStart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-status-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (PrWatchFactory first = new(folder, ownsFolder: false))
            {
                using HttpClient client = await Start(first);
                first.Services.GetRequiredService<StatusStore>().Update(status => status with
                {
                    LastPoll = first.Clock.GetUtcNow(),
                    Runs = status.Runs.SetItem("factory-7", new RunStatus { HeadSha = "def", ExitCode = 1 }),
                });
            }

            using PrWatchFactory second = new(folder, ownsFolder: false);
            using HttpClient again = await Start(second);
            using JsonDocument body = JsonDocument.Parse(await again.GetStringAsync(Route));

            Assert.Equal("2026-11-26T10:00:00.000Z", body.RootElement.GetProperty("lastPoll").GetString());
            JsonElement run = body.RootElement.GetProperty("runs").GetProperty("factory-7");
            Assert.Equal("def", run.GetProperty("headSha").GetString());
            Assert.Equal(1, run.GetProperty("exitCode").GetInt32());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"login\":{\"state\":\"maybe\"}}")]
    [InlineData("{\"surprise\":1}")]
    public async Task InvalidFile_IsIgnoredAtStart(string content)
    {
        using PrWatchFactory factory = new();
        File.WriteAllText(Path.Combine(factory.CreateDataFolder(), "status.json"), content);
        using HttpClient client = await Start(factory);

        Assert.Equal(Initial, await client.GetStringAsync(Route));
    }

    [Theory]
    [InlineData("\"x\"", false)]
    [InlineData("null", false)]
    [InlineData("\"runs_per_hour\"", true)]
    public void BrakeWithAnUnknownReason_LoadsAsNotHolding(string reason, bool kept)
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "status.json"),
                "{\"lastPoll\":\"2026-11-26T09:00:00.000Z\",\"brake\":{\"holding\":true,\"reason\":" + reason
                + ",\"until\":\"2026-11-26T11:00:00.000Z\"}}");

            Status loaded = new StatusStore(folder, new FakeClock(FakeClock.DefaultNow)).Current;

            Assert.Equal(FakeClock.DefaultNow.AddHours(-1), loaded.LastPoll);
            Assert.Equal(kept
                ? new BrakeStatus
                {
                    Holding = true,
                    Reason = TokenBrake.RunsPerHour,
                    Until = FakeClock.DefaultNow.AddHours(1),
                }
                : new BrakeStatus(), loaded.Brake);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void OldShapeFile_WithoutStageTestsOrMergeable_LoadsWithNulls()
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "status.json"),
                "{\"lastPoll\":\"2026-11-26T09:00:00.000Z\",\"prs\":[{\"project\":\"Alpha\",\"number\":4,"
                + "\"title\":\"T\",\"author\":\"a\",\"url\":null,\"headSha\":\"" + new string('a', 40) + "\","
                + "\"headSeenAt\":\"2026-11-26T08:00:00.000Z\",\"trusted\":false,\"request\":null,"
                + "\"round\":{\"k\":1,\"headSha\":\"" + new string('a', 40) + "\",\"current\":true,\"clean\":false}}]}");

            Status loaded = new StatusStore(folder, new FakeClock(FakeClock.DefaultNow)).Current;

            PrStatus pr = Assert.Single(loaded.Prs);
            Assert.Equal(4, pr.Number);
            Assert.Null(pr.Stage);
            Assert.NotNull(pr.Round);
            Assert.Null(pr.Round.Tests);
            Assert.Null(pr.Round.Mergeable);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("\"stage\":\"open\"", "")]
    [InlineData("", ",\"tests\":\"skipped\"")]
    public void StoredPrs_WithAnUnknownStageOrTests_LoadAsEmpty(string stage, string tests)
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string head = new('a', 40);
            File.WriteAllText(Path.Combine(folder, "status.json"),
                "{\"lastPoll\":\"2026-11-26T09:00:00.000Z\",\"prs\":[{\"project\":\"Alpha\",\"number\":4,"
                + "\"title\":\"T\",\"author\":\"a\",\"url\":null,\"headSha\":\"" + head + "\","
                + "\"headSeenAt\":\"2026-11-26T08:00:00.000Z\",\"trusted\":false,\"request\":null,"
                + (stage.Length > 0 ? stage + "," : "")
                + "\"round\":{\"k\":1,\"headSha\":\"" + head + "\",\"current\":true,\"clean\":false" + tests + "}}]}");

            Status loaded = new StatusStore(folder, new FakeClock(FakeClock.DefaultNow)).Current;

            Assert.Empty(loaded.Prs);
            Assert.Equal(FakeClock.DefaultNow.AddHours(-1), loaded.LastPoll);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Store_KeepsRunKeysInOrdinalOrder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            StatusStore store = new(folder, new FakeClock(FakeClock.DefaultNow));
            foreach (string key in new[] { "beta-1", "Alpha-2", "alpha-10" })
            {
                store.Update(status => status with { Runs = status.Runs.SetItem(key, new RunStatus()) });
            }

            StatusStore reloaded = new(folder, new FakeClock(FakeClock.DefaultNow));

            ImmutableArray<string> expected = ["Alpha-2", "alpha-10", "beta-1"];
            Assert.Equal(expected, store.Current.Runs.Keys);
            Assert.Equal(expected, reloaded.Current.Runs.Keys);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // --- job 444: every non-fatal write error is caught ---------------------------------------------

    [Theory]
    [MemberData(nameof(LedgerTests.NonFatalTypes), MemberType = typeof(LedgerTests))]
    public void WriterThatThrows_UpdateReturnsTheChange_WithStatusWriteFailed(string type)
    {
        using TempFolder temp = new();
        FakeClock clock = new(FakeClock.DefaultNow);
        StatusStore store = new(temp.Path, clock)
        {
            Writer = (_, _) => throw LedgerTests.Make(type, "status-marker"),
        };
        clock.Advance(TimeSpan.FromMinutes(3));

        Status next = store.Update(status => status with { LastPoll = FakeClock.DefaultNow });

        Assert.Equal(FakeClock.DefaultNow, next.LastPoll);
        Assert.Equal(new StatusError(StatusError.StatusWriteFailed, clock.GetUtcNow()), next.LastError);
        Assert.Same(next, store.Current);
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void AfterTheWriterWorksAgain_TheNextUpdate_WritesTheLatestStatus()
    {
        using TempFolder temp = new();
        FakeClock clock = new(FakeClock.DefaultNow);
        StatusStore store = new(temp.Path, clock)
        {
            Writer = (_, _) => throw new InvalidOperationException("status-marker"),
        };
        store.Update(status => status with { LastPoll = FakeClock.DefaultNow });

        store.Writer = PrivateFileWriter.Write;
        Status latest = store.Update(status => status with
        {
            Runs = status.Runs.SetItem("Alpha-1", new RunStatus { ExitCode = 0 }),
        });

        Status reloaded = new StatusStore(temp.Path, clock).Current;
        Assert.Equal(FakeClock.DefaultNow, reloaded.LastPoll);
        Assert.Equal(0, reloaded.Runs["Alpha-1"].ExitCode);
        Assert.Equal(StatusError.StatusWriteFailed, reloaded.LastError?.Code);
        Assert.Equal(JsonSerializer.Serialize(latest, PrWatchJson.FileOptions),
            JsonSerializer.Serialize(reloaded, PrWatchJson.FileOptions));
    }

    [Fact]
    public async Task WriterThatThrows_StoresNeitherTheMessageNorTheType_InTheRouteOrTheFile()
    {
        const string Marker = "secret-marker-7f3a";
        using PrWatchFactory factory = new();
        using HttpClient client = await Start(factory);
        StatusStore store = factory.Services.GetRequiredService<StatusStore>();
        UsageLedger ledger = factory.Services.GetRequiredService<UsageLedger>();
        ledger.Writer = (_, _) => throw new NotSupportedException(Marker);
        ledger.Start(factory.Clock.GetUtcNow());
        store.Writer = (_, _) => throw new InvalidOperationException(Marker);
        store.Update(status => status with { LastPoll = factory.Clock.GetUtcNow() });

        string body = await client.GetStringAsync(Route);
        Assert.Contains("\"lastError\":{\"code\":\"status_write_failed\",\"at\":\"2026-11-26T10:00:00.000Z\"}",
            body, StringComparison.Ordinal);

        store.Writer = PrivateFileWriter.Write;
        store.Update(status => status);
        string file = File.ReadAllText(StatusFile(factory));
        foreach (string text in new[] { body, file })
        {
            Assert.DoesNotContain(Marker, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperation", text, StringComparison.Ordinal);
            Assert.DoesNotContain("NotSupported", text, StringComparison.Ordinal);
        }

        using JsonDocument parsed = JsonDocument.Parse(file);
        Assert.Equal(["code", "at"],
            parsed.RootElement.GetProperty("lastError").EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException), false)]
    [InlineData(typeof(InsufficientExecutionStackException), false)]
    [InlineData(typeof(AccessViolationException), false)]
    [InlineData(typeof(ThreadInterruptedException), false)]
    [InlineData(typeof(IOException), true)]
    [InlineData(typeof(UnauthorizedAccessException), true)]
    [InlineData(typeof(InvalidOperationException), true)]
    [InlineData(typeof(NotSupportedException), true)]
    [InlineData(typeof(ArgumentException), true)]
    public void IsNonFatal_IsFalseOnlyForTheFatalRuntimeTypes(Type type, bool expected)
    {
        Exception exception = (Exception)Activator.CreateInstance(type)!;

        Assert.Equal(expected, StoreWrites.IsNonFatal(exception));
    }

    [Fact]
    public void WriterThatThrowsOutOfMemory_LeavesUpdate_AndKeepsTheStatus()
    {
        using TempFolder temp = new();
        StatusStore store = new(temp.Path, new FakeClock(FakeClock.DefaultNow))
        {
            Writer = (_, _) => throw new OutOfMemoryException(),
        };
        Status before = store.Current;

        Assert.Throws<OutOfMemoryException>(() => store.Update(status => status with { LastPoll = FakeClock.DefaultNow }));
        Assert.Same(before, store.Current);
    }

    [Fact]
    public void ChangeThatThrows_LeavesUpdate_AndKeepsTheStatus()
    {
        using TempFolder temp = new();
        StatusStore store = new(temp.Path, new FakeClock(FakeClock.DefaultNow));
        Status before = store.Current;

        Assert.Throws<InvalidOperationException>(() => store.Update(_ => throw new InvalidOperationException("change")));
        Assert.Same(before, store.Current);
        Assert.Null(store.Current.LastError);
    }

    [Fact]
    public void BeforeSaveThatThrows_LeavesUpdate_AndKeepsTheStatus()
    {
        using TempFolder temp = new();
        StatusStore store = new(temp.Path, new FakeClock(FakeClock.DefaultNow))
        {
            BeforeSave = _ => throw new InvalidOperationException("before-save"),
        };
        Status before = store.Current;

        Assert.Throws<InvalidOperationException>(() => store.Update(status => status with { LastPoll = FakeClock.DefaultNow }));
        Assert.Same(before, store.Current);
        Assert.Null(store.Current.LastError);
    }

    private static async Task<HttpClient> Start(PrWatchFactory factory)
    {
        using (HttpClient anonymous = factory.CreateClient())
        {
            using HttpResponseMessage health = await anonymous.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        return factory.CreateTokenClient();
    }

    private static string StatusFile(PrWatchFactory factory)
    {
        return Path.Combine(factory.DataFolder, "status.json");
    }

    /// <summary>A temp folder of its own, deleted on dispose.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "prwatch-status-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
