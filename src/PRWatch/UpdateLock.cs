using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PRWatch;

/// <summary>The update lock <c>&lt;data folder&gt;/.update.lock</c> that RoomKit's <c>updater.sh</c> and
/// <c>launch-agent.sh</c> take before they swap the server, and that PRWatch holds for the whole of every
/// <c>claude</c> run so a swap never kills a review. The lock is a folder made with POSIX <c>mkdir</c>, so only one
/// of the two can win. PRWatch's lock holds the marker file <c>prwatch-run</c>, one ASCII line
/// <c>&lt;pid&gt; &lt;start ticks&gt;\n</c> naming the process that holds it (job 461), so a second PRWatch on the
/// same data folder never removes a live run's lock. A lock folder without the marker, or a <c>.update.lock</c>
/// that is a symlink or a file, belongs to the updater and is never followed, changed or deleted.</summary>
internal sealed class UpdateLock
{
    public const string FolderName = ".update.lock";

    public const string MarkerName = "prwatch-run";

    /// <summary>The <see cref="Holder"/> while PRWatch holds the lock for a run.</summary>
    public const string Run = "run";

    /// <summary>The <see cref="Holder"/> for any other <c>.update.lock</c>.</summary>
    public const string Updater = "updater";

    public static readonly string[] Holders = [Run, Updater];

    /// <summary>The most bytes of the marker that are ever read.</summary>
    public const int MaxMarkerBytes = 64;

    /// <summary><c>0700</c>.</summary>
    private const int PrivateFolderMode = 448;

    private static readonly Lazy<long> OwnStartTicks = new(() =>
    {
        using Process own = Process.GetCurrentProcess();
        return StartTicks(own);
    });

    public UpdateLock(string dataFolder)
    {
        ArgumentNullException.ThrowIfNull(dataFolder);
        Path = System.IO.Path.Combine(dataFolder, FolderName);
        MarkerPath = System.IO.Path.Combine(Path, MarkerName);
    }

    /// <summary>The full path of <c>.update.lock</c>.</summary>
    public string Path { get; }

    /// <summary>The full path of the marker <c>prwatch-run</c> inside it.</summary>
    public string MarkerPath { get; }

    /// <summary>Makes the lock folder (<c>0700</c>) with a libc <c>mkdir</c>, which fails when anything is at the
    /// path already, then writes the marker naming this process (<see cref="OwnMarker"/>). False, with nothing left
    /// behind, when either step fails.</summary>
    public bool TryAcquire()
    {
        if (!MakeFolder(Path))
        {
            return false;
        }

        bool created = false;
        try
        {
            using FileStream marker = new(MarkerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            marker.Write(OwnMarker());
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (created)
            {
                TryDeleteMarker();
            }

            TryDeleteFolder();
            return false;
        }
    }

    /// <summary>The marker this process writes: <c>&lt;Environment.ProcessId&gt; &lt;start ticks&gt;\n</c> in ASCII,
    /// the ticks of the process's start time in UTC.</summary>
    public static byte[] OwnMarker()
    {
        return Marker(Environment.ProcessId, OwnStartTicks.Value);
    }

    /// <summary>The marker that names the process <paramref name="pid"/> started at
    /// <paramref name="startTicks"/>.</summary>
    public static byte[] Marker(int pid, long startTicks)
    {
        return Encoding.ASCII.GetBytes(pid.ToString(CultureInfo.InvariantCulture) + " "
            + startTicks.ToString(CultureInfo.InvariantCulture) + "\n");
    }

    /// <summary>The start ticks (UTC) of <paramref name="process"/>, as a marker records them.</summary>
    public static long StartTicks(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return process.StartTime.ToUniversalTime().Ticks;
    }

    /// <summary>Sets the lock folder's mtime to <paramref name="now"/>, only while PRWatch holds it, so the
    /// updater never counts a run's lock as stale.</summary>
    public void Touch(DateTimeOffset now)
    {
        if (Holder() != Run)
        {
            return;
        }

        try
        {
            Directory.SetLastWriteTimeUtc(Path, now.UtcDateTime);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next touch tries again.
        }
    }

    /// <summary>Deletes the marker and the lock folder, only when the folder (no symlink) holds the marker and the
    /// marker names this process; any other <c>.update.lock</c> is left as it is. Returns true when the lock is
    /// gone.</summary>
    public bool Release()
    {
        if (Holder() != Run)
        {
            return false;
        }

        Owner? owner = ReadOwner();
        if (owner is null || owner.Pid != Environment.ProcessId || owner.StartTicks != OwnStartTicks.Value)
        {
            return false;
        }

        return TryDeleteMarker() && TryDeleteFolder();
    }

    /// <summary>At app start: deletes the marker and the lock folder, only when the folder (no symlink) holds the
    /// marker and the marker's owner is not running (no owner, an exited process, or a PID whose start time is not
    /// the recorded one). A live owner's lock, and any other <c>.update.lock</c>, is left as it is. Returns true when
    /// the lock is gone.</summary>
    public bool ReleaseLeftBehind()
    {
        if (Holder() != Run)
        {
            return false;
        }

        Owner? owner = ReadOwner();
        if (owner is not null && IsAlive(owner))
        {
            return false;
        }

        return TryDeleteMarker() && TryDeleteFolder();
    }

    /// <summary>Who holds the lock: <c>null</c> when nothing is at the path, <see cref="Run"/> for a folder that
    /// holds the marker (a regular file), else <see cref="Updater"/> (a folder without the marker, a symlink, a
    /// file or anything that can't be read). Never follows a symlink.</summary>
    public string? Holder()
    {
        try
        {
            FileInfo entry = new(Path);
            if (entry.LinkTarget is not null)
            {
                return Updater;
            }

            if (File.Exists(Path))
            {
                return Updater;
            }

            if (!Directory.Exists(Path))
            {
                return null;
            }

            FileInfo marker = new(MarkerPath);
            return marker.LinkTarget is null && marker.Exists ? Run : Updater;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Updater;
        }
    }

    /// <summary>Parses <c>&lt;pid&gt; &lt;ticks&gt;</c> with an optional final <c>\n</c>: ASCII digits only, both
    /// positive, at most <see cref="MaxMarkerBytes"/> bytes; anything else is null.</summary>
    internal static Owner? ParseOwner(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaxMarkerBytes)
        {
            return null;
        }

        int length = bytes[^1] == (byte)'\n' ? bytes.Length - 1 : bytes.Length;
        int space = Array.IndexOf(bytes, (byte)' ', 0, length);
        if (space <= 0 || space >= length - 1)
        {
            return null;
        }

        for (int index = 0; index < length; index++)
        {
            if (index != space && bytes[index] is < (byte)'0' or > (byte)'9')
            {
                return null;
            }
        }

        string pidText = Encoding.ASCII.GetString(bytes, 0, space);
        string ticksText = Encoding.ASCII.GetString(bytes, space + 1, length - space - 1);
        if (!int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || !long.TryParse(ticksText, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            || pid <= 0 || ticks <= 0)
        {
            return null;
        }

        return new Owner(pid, ticks);
    }

    /// <summary>The owner the marker names, or null when the marker can't be read without following a symlink, is
    /// longer than <see cref="MaxMarkerBytes"/>, or is not two positive decimal integers. Its content is never
    /// logged or shown.</summary>
    private Owner? ReadOwner()
    {
        byte[]? bytes = ReadMarker();
        return bytes is null ? null : ParseOwner(bytes);
    }

    private byte[]? ReadMarker()
    {
        if (FolderHandleWriter.IsSupported)
        {
            return FolderHandleWriter.ReadNoFollow(Path, MarkerName, MaxMarkerBytes);
        }

        // No libc (a Windows test run; PRWatch installs on macOS only): Holder() has checked the marker is no
        // symlink.
        try
        {
            using FileStream stream = new(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] buffer = new byte[MaxMarkerBytes + 1];
            int total = 0;
            while (total < buffer.Length)
            {
                int count = stream.Read(buffer, total, buffer.Length - total);
                if (count == 0)
                {
                    break;
                }

                total += count;
            }

            return buffer[..total];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when the process <paramref name="owner"/> names is running and started at the recorded time. A
    /// process that exists but whose state or start time can't be read counts as alive: the lock is what stops a
    /// swap mid-review, so the bias is to keep it.</summary>
    private static bool IsAlive(Owner owner)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(owner.Pid);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }

        using (process)
        {
            try
            {
                return !process.HasExited && StartTicks(process) == owner.StartTicks;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
                or NotSupportedException)
            {
                return true;
            }
        }
    }

    private bool TryDeleteMarker()
    {
        try
        {
            File.Delete(MarkerPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes the lock folder when it is empty (never recursively).</summary>
    private bool TryDeleteFolder()
    {
        try
        {
            if (new DirectoryInfo(Path).LinkTarget is not null)
            {
                return false;
            }

            Directory.Delete(Path, recursive: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool MakeFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // No libc: the best a Windows test run can do (PRWatch installs on macOS only).
            if (Directory.Exists(path) || File.Exists(path))
            {
                return false;
            }

            Directory.CreateDirectory(path);
            return true;
        }

        return Mkdir(path, PrivateFolderMode) == 0;
    }

    /// <summary>The process a marker names.</summary>
    internal sealed record Owner(int Pid, long StartTicks);

    // DllImport, not LibraryImport: the generated code would need AllowUnsafeBlocks. On Unix the string is passed
    // as UTF-8.
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
