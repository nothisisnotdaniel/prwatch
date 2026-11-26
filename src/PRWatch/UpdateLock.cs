using System.Runtime.InteropServices;

namespace PRWatch;

/// <summary>The update lock <c>&lt;data folder&gt;/.update.lock</c> that RoomKit's <c>updater.sh</c> and
/// <c>launch-agent.sh</c> take before they swap the server, and that PRWatch holds for the whole of every
/// <c>claude</c> run so a swap never kills a review. The lock is a folder made with POSIX <c>mkdir</c>, so only one
/// of the two can win. PRWatch's lock holds the empty marker file <c>prwatch-run</c>; a lock folder without it, or
/// a <c>.update.lock</c> that is a symlink or a file, belongs to the updater and is never followed, changed or
/// deleted.</summary>
internal sealed class UpdateLock
{
    public const string FolderName = ".update.lock";

    public const string MarkerName = "prwatch-run";

    /// <summary>The <see cref="Holder"/> while PRWatch holds the lock for a run.</summary>
    public const string Run = "run";

    /// <summary>The <see cref="Holder"/> for any other <c>.update.lock</c>.</summary>
    public const string Updater = "updater";

    public static readonly string[] Holders = [Run, Updater];

    /// <summary><c>0700</c>.</summary>
    private const int PrivateFolderMode = 448;

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
    /// path already, then writes the marker. False, with nothing left behind, when either step fails.</summary>
    public bool TryAcquire()
    {
        if (!MakeFolder(Path))
        {
            return false;
        }

        try
        {
            using FileStream marker = new(MarkerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteFolder();
            return false;
        }
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

    /// <summary>Deletes the marker and the lock folder, only when the folder (no symlink) holds the marker; any
    /// other <c>.update.lock</c> is left as it is. Returns true when the lock is gone.</summary>
    public bool Release()
    {
        if (Holder() != Run)
        {
            return false;
        }

        try
        {
            File.Delete(MarkerPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return TryDeleteFolder();
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

    // DllImport, not LibraryImport: the generated code would need AllowUnsafeBlocks. On Unix the string is passed
    // as UTF-8.
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
