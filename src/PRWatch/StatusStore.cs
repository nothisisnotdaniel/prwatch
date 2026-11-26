using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PRWatch;

/// <summary>Holds the status in memory and writes it to <c>status.json</c> in the data folder after every
/// <see cref="Update"/>, atomically through <see cref="SafeFileWriter"/> as <c>0600</c> (never through a symlink).
/// A failed write keeps the status in memory with <c>lastError</c> <c>status_write_failed</c>. On start a valid
/// existing file is loaded; an invalid one is ignored (and replaced by the next update).</summary>
internal sealed class StatusStore
{
    public const string FileName = "status.json";

    public const long MaxFileBytes = 1024 * 1024;

    private readonly object gate = new();

    private readonly TimeProvider time;

    private Status current;

    public StatusStore(string folder, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(time);
        Path = System.IO.Path.Combine(folder, FileName);
        this.time = time;
        current = Load(Path);
    }

    /// <summary>The full path of <c>status.json</c>.</summary>
    public string Path { get; }

    /// <summary>The current status.</summary>
    public Status Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>Applies <paramref name="change"/> to the current status and saves it; calls are serialized, so
    /// no update is lost. Returns the new status.</summary>
    public Status Update(Func<Status, Status> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            Status next = change(current);
            try
            {
                PrivateFileWriter.Write(Path, JsonSerializer.SerializeToUtf8Bytes(next, PrWatchJson.FileOptions));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                next = next with { LastError = new StatusError(StatusError.StatusWriteFailed, time.GetUtcNow()) };
            }

            current = next;
            return next;
        }
    }

    /// <summary>Records <paramref name="code"/> as the last error, at the clock's time.</summary>
    public Status RecordError(string code)
    {
        return Update(status => status with { LastError = new StatusError(code, time.GetUtcNow()) });
    }

    /// <summary>The status in the file, or <see cref="Status.Initial"/> when it is missing or invalid. Invalid
    /// <c>prs</c>, <c>skipped</c> or <c>pick</c> content loads as the empty value and the rest still loads, a brake
    /// without a reason from <see cref="TokenBrake.Reasons"/> loads as not holding;
    /// a <c>runs</c> key that is not <c>&lt;repo&gt;-&lt;n&gt;</c> (<see cref="PrRules.IsRunKey"/>) is
    /// dropped.</summary>
    private static Status Load(string path)
    {
        byte[]? bytes = SafeFileReader.ReadBytes(path, MaxFileBytes);
        if (bytes is null)
        {
            return Status.Initial;
        }

        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject top)
            {
                return Status.Initial;
            }

            JsonNode? prs = Detach(top, "prs");
            JsonNode? skipped = Detach(top, "skipped");
            JsonNode? pick = Detach(top, "pick");
            Status? loaded = top.Deserialize<Status>(PrWatchJson.FileOptions);
            if (loaded is null || !IsValid(loaded))
            {
                return Status.Initial;
            }

            ImmutableSortedDictionary<string, RunStatus> runs = loaded.Runs
                .Where(run => PrRules.IsRunKey(run.Key))
                .ToImmutableSortedDictionary(run => run.Key, run => run.Value, StringComparer.Ordinal);
            return loaded with
            {
                Runs = runs,
                Brake = loaded.Brake.Reason is { } reason && TokenBrake.Reasons.Contains(reason, StringComparer.Ordinal)
                    ? loaded.Brake
                    : new BrakeStatus(),
                Prs = LoadPart<List<PrStatus>>(prs, list => list.Count <= PrRules.MaxPrs
                    && list.All(pr => pr is not null && PrRules.IsValid(pr))) ?? [],
                Skipped = LoadPart<List<SkipStatus>>(skipped, list => list.All(skip => skip is not null
                    && PrRules.IsProjectName(skip.Project)
                    && SkipStatus.Reasons.Contains(skip.Reason, StringComparer.Ordinal))) ?? [],
                Pick = LoadPart<PickStatus>(pick, found => PrRules.IsProjectName(found.Project)
                    && PrRules.IsNumber(found.Number)
                    && PrRules.IsSha(found.HeadSha)),
            };
        }
        catch (JsonException)
        {
            return Status.Initial;
        }
    }

    private static JsonNode? Detach(JsonObject top, string name)
    {
        if (!top.TryGetPropertyValue(name, out JsonNode? node))
        {
            return null;
        }

        top.Remove(name);
        return node;
    }

    /// <summary>The part read from <paramref name="node"/> when it parses and <paramref name="valid"/> accepts
    /// it, else <c>null</c>.</summary>
    private static T? LoadPart<T>(JsonNode? node, Func<T, bool> valid)
        where T : class
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            T? part = node.Deserialize<T>(PrWatchJson.FileOptions);
            return part is not null && valid(part) ? part : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsValid(Status status)
    {
        return status.Runs is not null
            && status.Runs.Values.All(run => run is not null)
            && status.Brake is not null
            && status.Login is not null
            && LoginStatus.LoginStates.Contains(status.Login.State, StringComparer.Ordinal)
            && (status.UpdateLock is null || UpdateLock.Holders.Contains(status.UpdateLock, StringComparer.Ordinal))
            && (status.LastError is null || status.LastError.Code is not null);
    }
}
