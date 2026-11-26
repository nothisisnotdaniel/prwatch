using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PRWatch;

/// <summary>The outcome of one poll: the parsed output (an object with <c>prs</c> and <c>skipped</c> arrays), or
/// one of the fixed <see cref="StatusError"/> poll codes.</summary>
internal sealed class PollOutcome : IDisposable
{
    private PollOutcome(JsonDocument? output, string? error)
    {
        Output = output;
        Error = error;
    }

    public JsonDocument? Output { get; }

    public string? Error { get; }

    public static PollOutcome Ok(JsonDocument output)
    {
        return new PollOutcome(output, null);
    }

    public static PollOutcome Failed(string error)
    {
        return new PollOutcome(null, error);
    }

    public void Dispose()
    {
        Output?.Dispose();
    }
}

/// <summary>Runs <c>python3 &lt;root&gt;/factory/tools/job.py pr poll</c>: the first <c>python3</c> on
/// <see cref="FixedPath"/>, a fixed <see cref="ProcessStartInfo.ArgumentList"/>, no shell, cwd the factory root,
/// stdin closed and only <c>HOME</c>, <c>USER</c>, <c>TMPDIR</c> and <c>PATH</c> (= <see cref="FixedPath"/>) in the
/// environment. A run over the timeout is killed with its process tree. Stdout is capped at
/// <see cref="MaxOutputBytes"/>; stderr is read up to <see cref="MaxErrorBytes"/> and thrown away, never stored or
/// logged.</summary>
internal sealed class PollRunner
{
    /// <summary>ControlRoom's <c>FIXED_PATH</c>.</summary>
    public const string FixedPath = "/usr/local/share/dotnet:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";

    public const int MaxOutputBytes = 4 * 1024 * 1024;

    public const int MaxErrorBytes = 64 * 1024;

    public const int DefaultTimeoutSeconds = 120;

    /// <summary>The variables passed on from PRWatch's own environment (<c>PATH</c> is always
    /// <see cref="FixedPath"/>).</summary>
    public static readonly string[] PassedVariables = ["HOME", "USER", "TMPDIR"];

    private readonly string searchPath;

    public PollRunner(TimeSpan timeout, string searchPath = FixedPath)
    {
        ArgumentNullException.ThrowIfNull(searchPath);
        Timeout = timeout;
        this.searchPath = searchPath;
    }

    public TimeSpan Timeout { get; }

    /// <summary>A runner with the timeout from the config value <c>PollTimeoutSeconds</c> (default 120).</summary>
    public static PollRunner FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        int seconds = configuration.GetValue("PollTimeoutSeconds", DefaultTimeoutSeconds);
        return new PollRunner(TimeSpan.FromSeconds(seconds > 0 ? seconds : DefaultTimeoutSeconds));
    }

    /// <summary>The first executable <c>python3</c> in the absolute folders of <paramref name="path"/>, or
    /// <c>null</c>.</summary>
    public static string? FindPython(string path = FixedPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (string folder in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                continue;
            }

            string candidate = Path.Combine(folder, "python3");
            if (SettingsRules.IsExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Runs the poll in <paramref name="root"/>. Cancelling kills the poll with its process tree and
    /// throws <see cref="OperationCanceledException"/>.</summary>
    public async Task<PollOutcome> RunAsync(string root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        string? python = FindPython(searchPath);
        if (python is null)
        {
            return PollOutcome.Failed(StatusError.PythonMissing);
        }

        ProcessStartInfo start = new(python)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(root, "factory", "tools", "job.py"));
        start.ArgumentList.Add("pr");
        start.ArgumentList.Add("poll");
        start.Environment.Clear();
        foreach (string name in PassedVariables)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                start.Environment[name] = value;
            }
        }

        start.Environment["PATH"] = FixedPath;

        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return PollOutcome.Failed(StatusError.PollFailed);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return PollOutcome.Failed(StatusError.PollFailed);
        }

        process.StandardInput.Close();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenRegistration killer = timeout.Token.Register(() => Kill(process));
        timeout.CancelAfter(Timeout);
        Task<byte[]?> output = ReadCapped(process.StandardOutput.BaseStream, MaxOutputBytes, timeout.Token);
        Task error = Drain(process.StandardError.BaseStream, MaxErrorBytes, timeout.Token);
        byte[]? bytes = await Settle(output);
        if (bytes is null)
        {
            Kill(process);
        }

        await Settle(error);
        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested)
        {
            return PollOutcome.Failed(StatusError.PollTimeout);
        }

        if (bytes is null)
        {
            return PollOutcome.Failed(StatusError.PollOutputInvalid);
        }

        if (process.ExitCode != 0)
        {
            return PollOutcome.Failed(StatusError.PollFailed);
        }

        return Parse(bytes);
    }

    /// <summary>The task's result, or the default when it was cancelled or its pipe failed.</summary>
    private static async Task<T?> Settle<T>(Task<T?> task)
    {
        try
        {
            return await task;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            return default;
        }
    }

    private static async Task Settle(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // Stderr is thrown away anyway.
        }
    }

    /// <summary>The output when it is a JSON object with <c>prs</c> and <c>skipped</c> arrays, else
    /// <c>poll_output_invalid</c>.</summary>
    public static PollOutcome Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return PollOutcome.Failed(StatusError.PollOutputInvalid);
        }

        JsonElement top = document.RootElement;
        if (top.ValueKind != JsonValueKind.Object
            || !top.TryGetProperty("prs", out JsonElement prs) || prs.ValueKind != JsonValueKind.Array
            || !top.TryGetProperty("skipped", out JsonElement skipped) || skipped.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            return PollOutcome.Failed(StatusError.PollOutputInvalid);
        }

        return PollOutcome.Ok(document);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>The whole stream, or <c>null</c> as soon as it passes <paramref name="max"/> bytes.</summary>
    private static async Task<byte[]?> ReadCapped(Stream stream, int max, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Reads the stream to its end so the child never blocks on a full pipe, keeping at most
    /// <paramref name="max"/> bytes, which are then thrown away.</summary>
    private static async Task Drain(Stream stream, int max, CancellationToken cancellationToken)
    {
        byte[] kept = new byte[max];
        int length = 0;
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            int take = Math.Min(read, max - length);
            Array.Copy(chunk, 0, kept, length, take);
            length += take;
        }

        Array.Clear(kept, 0, length);
    }
}
