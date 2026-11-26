using System.Text.Json;

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

    private static Status Load(string path)
    {
        byte[]? bytes = SafeFileReader.ReadBytes(path, MaxFileBytes);
        if (bytes is null)
        {
            return Status.Initial;
        }

        try
        {
            Status? loaded = JsonSerializer.Deserialize<Status>(bytes, PrWatchJson.FileOptions);
            return loaded is not null && IsValid(loaded)
                ? loaded with { Runs = loaded.Runs.WithComparers(StringComparer.Ordinal) }
                : Status.Initial;
        }
        catch (JsonException)
        {
            return Status.Initial;
        }
    }

    private static bool IsValid(Status status)
    {
        return status.Runs is not null
            && status.Runs.Values.All(run => run is not null)
            && status.Brake is not null
            && status.Login is not null
            && LoginStatus.LoginStates.Contains(status.Login.State, StringComparer.Ordinal)
            && (status.LastError is null || status.LastError.Code is not null);
    }
}
