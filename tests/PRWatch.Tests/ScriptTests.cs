using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace PRWatch.Tests;

/// <summary>RoomKit's <c>scripts/launch-agent.sh</c> and <c>scripts/updater.sh</c> as PRWatch ships them (job 428,
/// AC-1 and AC-6): byte copies of the pinned package's files outside the <c>roomkit:app</c> block, and the real
/// updater ending <c>busy</c> while PRWatch holds the update lock. The updater runs only on macOS, in its own temp
/// <c>HOME</c>, with fake <c>launchctl</c>, <c>lsof</c> and <c>curl</c> first on its <c>PATH</c>.</summary>
public sealed partial class ScriptTests
{
    private const string BlockBegin = "# roomkit:app-begin\n";

    private const string BlockEnd = "# roomkit:app-end\n";

    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] Block =
    [
        "APP_NAME=\"PRWatch\"",
        "LABEL=\"local.prwatch.server\"",
        "UPDATER_LABEL=\"local.prwatch.updater\"",
        "SUPPORT_FOLDER=\"PRWatch\"",
        "LOG_FOLDER=\"PRWatch\"",
        "REPO_SLUG=\"nothisisnotdaniel/prwatch\"",
        "RELEASES=\"https://github.com/nothisisnotdaniel/prwatch/releases\"",
        "APP_PORT=8101",
        "TARBALL_PREFIX=\"prwatch\"",
        "SERVER_NAME=\"PRWatch\"",
        "SOLUTION_FILE=\"PRWatch.slnx\"",
    ];

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode Executable = Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    // --- AC-1: byte copies outside the app block -----------------------------------------------

    [Theory]
    [InlineData("launch-agent.sh")]
    [InlineData("updater.sh")]
    public void Script_IsThePinnedPackagesCopy_OutsideTheAppBlock_WithPRWatchsValues(string name)
    {
        byte[] ours = File.ReadAllBytes(RepoFiles.PathOf("scripts/" + name));
        string package = Path.Combine(PackageFolder(), "scripts", name);
        byte[] theirs = File.ReadAllBytes(package);

        (byte[] ourRest, string ourBlock) = Cut(ours);
        (byte[] theirRest, _) = Cut(theirs);
        Assert.Equal(theirRest, ourRest);
        Assert.Equal(Block, ourBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Executable, File.GetUnixFileMode(RepoFiles.PathOf("scripts/" + name)));
        }
    }

    // --- AC-6: the real updater while PRWatch holds the lock -----------------------------------

    [Fact]
    public async Task Updater_WhilePRWatchHoldsTheLock_EndsBusy_WithoutANetworkCall()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string folder = NewFolder();
        try
        {
            Install install = Arrange(folder, withSettings: true);
            UpdateLock updateLock = new(install.Support);
            Assert.True(updateLock.TryAcquire());

            string last = await RunUpdater(install);

            Assert.EndsWith(" busy v0.1.0", last, StringComparison.Ordinal);
            Assert.False(File.Exists(install.CurlCalls));
            Assert.Equal(UpdateLock.Run, updateLock.Holder());
            Assert.True(updateLock.Release());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Updater_WithoutTheSettingsFile_EndsOff()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string folder = NewFolder();
        try
        {
            Install install = Arrange(folder, withSettings: false);
            UpdateLock updateLock = new(install.Support);
            Assert.True(updateLock.TryAcquire());

            string last = await RunUpdater(install);

            Assert.EndsWith(" off", last, StringComparison.Ordinal);
            Assert.False(File.Exists(install.CurlCalls));
            Assert.Equal(UpdateLock.Run, updateLock.Holder());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>The pinned RoomKit package: <c>&lt;NuGet packages&gt;/roomkit/&lt;version&gt;</c>, the version read
    /// from <c>src/PRWatch/PRWatch.csproj</c>.</summary>
    private static string PackageFolder()
    {
        Match pinned = PinPattern().Match(RepoFiles.Read("src/PRWatch/PRWatch.csproj"));
        Assert.True(pinned.Success, "RoomKit version pin not found");
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } set
            ? set
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        return Path.Combine(packages, "roomkit", pinned.Groups["v"].Value);
    }

    /// <summary>The file without its one app block (begin and end lines included), and the lines between.</summary>
    private static (byte[] Outside, string Inside) Cut(byte[] file)
    {
        string text = Encoding.Latin1.GetString(file);
        int begin = text.IndexOf(BlockBegin, StringComparison.Ordinal);
        Assert.True(begin >= 0, "no app-begin line");
        Assert.Equal(begin, text.LastIndexOf(BlockBegin, StringComparison.Ordinal));
        int end = text.IndexOf(BlockEnd, begin, StringComparison.Ordinal);
        Assert.True(end > begin, "no app-end line");
        Assert.Equal(end, text.LastIndexOf(BlockEnd, StringComparison.Ordinal));
        string rest = text[..begin] + text[(end + BlockEnd.Length)..];
        return (Encoding.Latin1.GetBytes(rest), text[(begin + BlockBegin.Length)..end]);
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A release install in <c>&lt;folder&gt;/home</c>: the private support folder with
    /// <c>installed-release</c> <c>v0.1.0</c>, <c>server/</c> and (when asked) <c>settings.json</c>
    /// <c>{"autoUpdate":true}</c>, the server plist, an empty <c>factory/jobs/</c> in <c>&lt;folder&gt;/root</c> and
    /// a fake bin with <c>launchctl</c> and <c>lsof</c> (exit 1, print nothing) and <c>curl</c> (logs a call,
    /// exit 1).</summary>
    [SupportedOSPlatform("macos")]
    private static Install Arrange(string folder, bool withSettings)
    {
        string home = Path.Combine(folder, "home");
        string support = Path.Combine(home, "Library", "Application Support", "PRWatch");
        Directory.CreateDirectory(support);
        File.SetUnixFileMode(support, Private);
        if (withSettings)
        {
            WritePrivate(Path.Combine(support, "settings.json"), "{\"autoUpdate\":true}");
        }

        WritePrivate(Path.Combine(support, "installed-release"), "v0.1.0\n");
        Directory.CreateDirectory(Path.Combine(support, "server"));
        string agents = Path.Combine(home, "Library", "LaunchAgents");
        Directory.CreateDirectory(agents);
        File.WriteAllText(Path.Combine(agents, "local.prwatch.server.plist"), "<plist/>\n");
        string root = Path.Combine(folder, "root");
        Directory.CreateDirectory(Path.Combine(root, "factory", "jobs"));

        string bin = Path.Combine(folder, "fake-bin");
        Directory.CreateDirectory(bin);
        string curlCalls = Path.Combine(folder, "curl-calls");
        WriteTool(Path.Combine(bin, "launchctl"), "#!/bin/sh\nexit 1\n");
        WriteTool(Path.Combine(bin, "lsof"), "#!/bin/sh\nexit 1\n");
        WriteTool(Path.Combine(bin, "curl"), "#!/bin/sh\necho called >> '" + curlCalls + "'\nexit 1\n");
        return new Install(home, support, root, bin, curlCalls);
    }

    [SupportedOSPlatform("macos")]
    private static void WritePrivate(string path, string text)
    {
        File.WriteAllText(path, text, new UTF8Encoding(false));
        File.SetUnixFileMode(path, PrivateFile);
    }

    [SupportedOSPlatform("macos")]
    private static void WriteTool(string path, string text)
    {
        File.WriteAllText(path, text, new UTF8Encoding(false));
        File.SetUnixFileMode(path, Private);
    }

    /// <summary>Runs <c>/bin/sh updater.sh --factory-root &lt;root&gt;</c> with only <c>HOME</c> and
    /// <c>PATH</c> set, waits for its exit within <see cref="ScriptTimeout"/> and returns the last stdout
    /// line.</summary>
    private static async Task<string> RunUpdater(Install install)
    {
        ProcessStartInfo start = new("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = install.Root,
        };
        start.ArgumentList.Add(RepoFiles.PathOf("scripts/updater.sh"));
        start.ArgumentList.Add("--factory-root");
        start.ArgumentList.Add(install.Root);
        start.Environment.Clear();
        start.Environment["HOME"] = install.Home;
        start.Environment["PATH"] = install.Bin + ":/usr/bin:/bin";
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(ScriptTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("updater.sh did not exit within " + ScriptTimeout.TotalSeconds + " s");
        }

        string text = await output;
        await error;
        Assert.Equal(0, process.ExitCode);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        return lines[^1];
    }

    [GeneratedRegex("Include=\"RoomKit\" Version=\"\\[(?<v>[0-9A-Za-z.-]+)\\]\"", RegexOptions.CultureInvariant)]
    private static partial Regex PinPattern();

    private sealed record Install(string Home, string Support, string Root, string Bin, string CurlCalls);
}
