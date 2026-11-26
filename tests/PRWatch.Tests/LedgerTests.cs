using System.Text;
using System.Text.Json;

namespace PRWatch.Tests;

/// <summary>The usage ledger (backlog item 5, AC-9 and AC-10): reload, pruning, the file mode, a symlink in place
/// of the file, and junk or over-size content. Each test has its own temp folder and <see cref="FakeClock"/>.</summary>
public sealed class LedgerTests : IDisposable
{
    private readonly string folder;

    private readonly FakeClock clock = new(FakeClock.DefaultNow);

    private readonly StatusStore status;

    public LedgerTests()
    {
        folder = Path.Combine(Path.GetTempPath(), "prwatch-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        status = new StatusStore(folder, clock);
    }

    private string File => Path.Combine(folder, UsageLedger.FileName);

    [Fact]
    public void Entries_SurviveARestart()
    {
        UsageLedger ledger = new(folder, status, clock);
        LedgerEntry first = ledger.Start(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(20));
        ledger.End(first, clock.GetUtcNow(), 0.6);
        clock.Advance(TimeSpan.FromMinutes(1));
        ledger.Start(clock.GetUtcNow());

        UsageLedger reloaded = new(folder, status, clock);

        Assert.Equal(
            [
                new LedgerEntry
                {
                    StartedAt = FakeClock.DefaultNow,
                    EndedAt = FakeClock.DefaultNow.AddMinutes(20),
                    CostUsd = 0.6,
                },
                new LedgerEntry { StartedAt = FakeClock.DefaultNow.AddMinutes(21) },
            ],
            reloaded.Entries);
        Assert.Null(status.Current.LastError);
    }

    [Fact]
    public void File_HoldsNumbersAndTimesOnly()
    {
        UsageLedger ledger = new(folder, status, clock);
        LedgerEntry entry = ledger.Start(clock.GetUtcNow());
        ledger.End(entry, clock.GetUtcNow().AddMinutes(5), null);

        using JsonDocument file = JsonDocument.Parse(System.IO.File.ReadAllBytes(File));
        JsonElement only = Assert.Single(file.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal(["startedAt", "endedAt", "costUsd"], only.EnumerateObject().Select(property => property.Name));
        Assert.Equal("2026-11-26T10:00:00.000Z", only.GetProperty("startedAt").GetString());
        Assert.Equal("2026-11-26T10:05:00.000Z", only.GetProperty("endedAt").GetString());
        Assert.Equal(JsonValueKind.Null, only.GetProperty("costUsd").ValueKind);
    }

    [Fact]
    public void EntriesOlderThanSevenDays_AreGoneAfterTheNextWrite()
    {
        UsageLedger ledger = new(folder, status, clock);
        ledger.Start(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromDays(1));
        ledger.Start(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromDays(7));

        Assert.Equal(2, new UsageLedger(folder, status, clock).Entries.Count);
        ledger.Start(clock.GetUtcNow());

        Assert.Equal([FakeClock.DefaultNow.AddDays(1), FakeClock.DefaultNow.AddDays(8)],
            ledger.Entries.Select(entry => entry.StartedAt));
        Assert.Equal(2, new UsageLedger(folder, status, clock).Entries.Count);
    }

    [Fact]
    public void Entries_AreCappedAtTheMaximum_DroppingTheOldest()
    {
        UsageLedger ledger = new(folder, status, clock);
        for (int index = 0; index < UsageLedger.MaxEntries + 2; index++)
        {
            ledger.Start(clock.GetUtcNow());
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(UsageLedger.MaxEntries, ledger.Entries.Count);
        Assert.Equal(FakeClock.DefaultNow.AddSeconds(2), ledger.Entries[0].StartedAt);
        Assert.Equal(UsageLedger.MaxEntries, new UsageLedger(folder, status, clock).Entries.Count);
    }

    [Fact]
    public void File_IsPrivate()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        new UsageLedger(folder, status, clock).Start(clock.GetUtcNow());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(File));
    }

    [Fact]
    public void ASymlinkAtThePath_IsNotFollowed_AndTheEntriesStillCountInMemory()
    {
        string outside = Path.Combine(folder, "outside.json");
        System.IO.File.WriteAllText(outside, "keep me\n");
        byte[] before = System.IO.File.ReadAllBytes(outside);
        System.IO.File.CreateSymbolicLink(File, outside);
        UsageLedger ledger = new(folder, status, clock);
        Settings settings = new();

        for (int index = 0; index < settings.BrakeRunsPerHour; index++)
        {
            ledger.Start(clock.GetUtcNow());
        }

        Assert.Equal(before, System.IO.File.ReadAllBytes(outside));
        Assert.NotNull(new FileInfo(File).LinkTarget);
        Assert.Equal(StatusError.LedgerWriteFailed, status.Current.LastError?.Code);
        Assert.Equal(settings.BrakeRunsPerHour, ledger.Entries.Count);
        Assert.Equal(TokenBrake.RunsPerHour,
            TokenBrake.Evaluate(null, ledger.Entries, settings, clock.GetUtcNow()).Reason);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{\"entries\":null}")]
    [InlineData("{\"entries\":[null]}")]
    [InlineData("{\"entries\":[{\"endedAt\":\"2026-11-26T10:00:00.000Z\"}]}")]
    [InlineData("{\"entries\":[{\"startedAt\":\"yesterday\"}]}")]
    [InlineData("{\"entries\":[{\"startedAt\":\"2026-11-26T10:00:00.000Z\",\"title\":\"x\"}]}")]
    [InlineData("{\"entries\":[{\"startedAt\":\"2026-11-26T10:00:00.000Z\",\"costUsd\":true}]}")]
    public void InvalidContent_LoadsAsEmpty(string content)
    {
        System.IO.File.WriteAllText(File, content);

        Assert.Empty(new UsageLedger(folder, status, clock).Entries);
    }

    [Fact]
    public void OverSizeContent_LoadsAsEmpty()
    {
        StringBuilder json = new("{\"entries\":[");
        string entry = "{\"startedAt\":\"2026-11-26T10:00:00.000Z\",\"endedAt\":null,\"costUsd\":null}";
        json.Append(entry);
        // Valid JSON (whitespace padding) that is only too big.
        json.Append(' ', (int)UsageLedger.MaxFileBytes);
        json.Append(',').Append(entry).Append("]}");
        System.IO.File.WriteAllText(File, json.ToString());

        Assert.Empty(new UsageLedger(folder, status, clock).Entries);
    }

    [Fact]
    public void ValidContent_Loads()
    {
        System.IO.File.WriteAllText(File,
            "{\"entries\":[{\"startedAt\":\"2026-11-26T10:00:00.000Z\",\"endedAt\":null,\"costUsd\":1.5}]}");

        Assert.Equal(1.5, Assert.Single(new UsageLedger(folder, status, clock).Entries).CostUsd);
    }

    // --- job 444: every non-fatal write error is caught ---------------------------------------------

    public static TheoryData<string> NonFatalTypes => ["invalid", "notsupported", "argument"];

    internal static Exception Make(string type, string message)
    {
        return type switch
        {
            "invalid" => new InvalidOperationException(message),
            "notsupported" => new NotSupportedException(message),
            "argument" => new ArgumentException(message),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    [Theory]
    [MemberData(nameof(NonFatalTypes))]
    public void WriterThatThrows_KeepsTheEntriesInMemory_AndRecordsLedgerWriteFailed(string type)
    {
        UsageLedger ledger = new(folder, status, clock)
        {
            Writer = (_, _) => throw Make(type, "ledger-marker"),
        };

        LedgerEntry entry = ledger.Start(clock.GetUtcNow());
        Assert.Equal(StatusError.LedgerWriteFailed, status.Current.LastError?.Code);
        Assert.Equal(entry, Assert.Single(ledger.Entries));
        clock.Advance(TimeSpan.FromMinutes(4));
        ledger.End(entry, clock.GetUtcNow(), 0.75);

        LedgerEntry ended = Assert.Single(ledger.Entries);
        Assert.Equal(FakeClock.DefaultNow, ended.StartedAt);
        Assert.Equal(FakeClock.DefaultNow.AddMinutes(4), ended.EndedAt);
        Assert.Equal(0.75, ended.CostUsd);
        Assert.Equal(new StatusError(StatusError.LedgerWriteFailed, clock.GetUtcNow()), status.Current.LastError);
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public void AfterTheWriterWorksAgain_TheNextSave_WritesEveryInMemoryEntry()
    {
        UsageLedger ledger = new(folder, status, clock)
        {
            Writer = (_, _) => throw new InvalidOperationException("ledger-marker"),
        };
        LedgerEntry first = ledger.Start(clock.GetUtcNow());
        ledger.End(first, clock.GetUtcNow().AddMinutes(2), 0.5);

        ledger.Writer = PrivateFileWriter.Write;
        clock.Advance(TimeSpan.FromMinutes(5));
        ledger.Start(clock.GetUtcNow());

        Assert.Equal(ledger.Entries, new UsageLedger(folder, status, clock).Entries);
        Assert.Equal(2, ledger.Entries.Count);
    }

    [Fact]
    public void WriterThatThrowsOutOfMemory_LeavesStart()
    {
        UsageLedger ledger = new(folder, status, clock)
        {
            Writer = (_, _) => throw new OutOfMemoryException(),
        };

        Assert.Throws<OutOfMemoryException>(() => ledger.Start(clock.GetUtcNow()));
        Assert.Null(status.Current.LastError);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
