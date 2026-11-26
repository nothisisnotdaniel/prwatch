using System.Text.Json;

namespace PRWatch;

/// <summary>One started run in the ledger: when it started, and when it ended with the cost of its last
/// <c>result</c> (numbers and times only).</summary>
internal sealed record LedgerEntry
{
    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public double? CostUsd { get; init; }
}

/// <summary>The usage ledger (<c>usage-ledger.json</c> in the data folder): one entry per started run, at most
/// <see cref="MaxEntries"/>. Every write first drops the entries that started more than <see cref="Keep"/> ago and
/// goes atomically through <see cref="PrivateFileWriter"/> as <c>0600</c> (never through a symlink). A failed
/// serialization or write (any exception <see cref="StoreWrites.IsNonFatal"/> accepts) keeps the entries in memory
/// and records only the fixed code <c>ledger_write_failed</c>; a fatal runtime exception leaves the call. A missing, invalid or over-1-MiB file loads
/// as empty.</summary>
internal sealed class UsageLedger
{
    public const string FileName = "usage-ledger.json";

    public const long MaxFileBytes = 1024 * 1024;

    public const int MaxEntries = 1000;

    /// <summary>How long an entry is kept after its start.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    private readonly object gate = new();

    private readonly StatusStore status;

    private readonly TimeProvider time;

    private readonly List<LedgerEntry> entries;

    public UsageLedger(string folder, StatusStore status, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(time);
        Path = System.IO.Path.Combine(folder, FileName);
        this.status = status;
        this.time = time;
        entries = Load(Path);
    }

    /// <summary>The full path of <c>usage-ledger.json</c>.</summary>
    public string Path { get; }

    /// <summary>Test-only: writes the serialized ledger to its path, so a test can make a save fail.
    /// <see cref="PrivateFileWriter.Write"/> in production; never set from config or a request.</summary>
    internal Action<string, byte[]> Writer { get; set; } = PrivateFileWriter.Write;

    /// <summary>A copy of the entries, oldest start first.</summary>
    public IReadOnlyList<LedgerEntry> Entries
    {
        get
        {
            lock (gate)
            {
                return [.. entries];
            }
        }
    }

    /// <summary>Adds a run that started at <paramref name="startedAt"/> and saves the ledger. The returned entry is
    /// the handle for <see cref="End"/>.</summary>
    public LedgerEntry Start(DateTimeOffset startedAt)
    {
        LedgerEntry entry = new() { StartedAt = startedAt };
        bool saved;
        lock (gate)
        {
            entries.Add(entry);
            saved = Save();
        }

        Report(saved);
        return entry;
    }

    /// <summary>Sets <paramref name="entry"/>'s end and cost and saves the ledger; an entry that is no longer in the
    /// ledger changes nothing.</summary>
    public void End(LedgerEntry entry, DateTimeOffset endedAt, double? costUsd)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool saved;
        lock (gate)
        {
            int index = entries.FindIndex(found => ReferenceEquals(found, entry));
            if (index < 0)
            {
                return;
            }

            entries[index] = entry with { EndedAt = endedAt, CostUsd = costUsd };
            saved = Save();
        }

        Report(saved);
    }

    /// <summary>Prunes and writes the entries; false when the write failed. Called under <see cref="gate"/>.</summary>
    private bool Save()
    {
        DateTimeOffset cutoff = time.GetUtcNow() - Keep;
        entries.RemoveAll(entry => entry.StartedAt < cutoff);
        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(0, entries.Count - MaxEntries);
        }

        try
        {
            Writer(Path, JsonSerializer.SerializeToUtf8Bytes(
                new LedgerFile { Entries = [.. entries] }, PrWatchJson.FileOptions));
            return true;
        }
        catch (Exception exception) when (StoreWrites.IsNonFatal(exception))
        {
            return false;
        }
    }

    private void Report(bool saved)
    {
        if (!saved)
        {
            status.RecordError(StatusError.LedgerWriteFailed);
        }
    }

    private static List<LedgerEntry> Load(string path)
    {
        byte[]? bytes = SafeFileReader.ReadBytes(path, MaxFileBytes);
        if (bytes is null)
        {
            return [];
        }

        try
        {
            LedgerFile? file = JsonSerializer.Deserialize<LedgerFile>(bytes, PrWatchJson.FileOptions);
            if (file?.Entries is not { } loaded
                || loaded.Count > MaxEntries
                || !loaded.All(entry => entry is not null
                    && (entry.CostUsd is null || double.IsFinite(entry.CostUsd.Value))))
            {
                return [];
            }

            return [.. loaded.OrderBy(entry => entry.StartedAt)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record LedgerFile
    {
        public List<LedgerEntry>? Entries { get; init; }
    }
}
