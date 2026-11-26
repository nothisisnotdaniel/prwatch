using System.Diagnostics;
using System.Runtime.Versioning;

namespace PRWatch.Tests;

/// <summary>Runs <c>scripts/restore.sh</c> (a copy in its own temp repo folder) with <c>/bin/sh</c>, a temp
/// <c>HOME</c> and fake <c>security</c> and <c>dotnet</c> first on its <c>PATH</c>: the GitHub Packages token reaches
/// only the one <c>dotnet restore</c>, from the keychain or a private 0600 file, and is never printed. macOS only (the
/// script reads the keychain and uses BSD <c>stat</c>).</summary>
public sealed class RestoreScriptTests : IDisposable
{
    private const string User = "octo-user";
    private const string Token = "ghp_Secret123";
    private static readonly TimeSpan ProcessLimit = TimeSpan.FromSeconds(10);

    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly string folder = Directory.CreateTempSubdirectory("prwatch-restore-").FullName;
    private readonly string repo;
    private readonly string home;
    private readonly string bin;
    private readonly string dotnetLog;
    private readonly string securityLog;
    private readonly string keychain;
    private readonly string tokenFile;

    public RestoreScriptTests()
    {
        repo = Path.Combine(folder, "repo");
        home = Path.Combine(folder, "home");
        bin = Path.Combine(folder, "fakebin");
        dotnetLog = Path.Combine(folder, "dotnet-calls");
        securityLog = Path.Combine(folder, "security-calls");
        keychain = Path.Combine(folder, "keychain");
        tokenFile = Path.Combine(home, "Library", "Application Support", "PRWatch", "github-packages");
        Directory.CreateDirectory(Path.Combine(repo, "scripts"));
        Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(keychain);
        File.Copy(RepoFiles.PathOf("scripts/restore.sh"), Path.Combine(repo, "scripts", "restore.sh"));
        File.WriteAllText(Path.Combine(repo, "restore-marker"), "");
        File.WriteAllText(Path.Combine(folder, "dotnet-exit"), "0\n");
        WriteFake(
            "dotnet",
            "{ printf 'argv:'; printf ' %s' \"$@\"; printf '\\n'; if [ -f restore-marker ]; then echo 'cwd: repo root'; else echo 'cwd: elsewhere'; fi;" +
            " printf 'cred: %s\\n' \"${NuGetPackageSourceCredentials_github:-<unset>}\"; } >> '" + dotnetLog + "'\n" +
            "exit \"$(cat '" + Path.Combine(folder, "dotnet-exit") + "')\"\n");
        WriteFake(
            "security",
            "printf '%s\\n' \"$*\" >> '" + securityLog + "'\n" +
            "[ \"$1 $2 $3\" = \"find-generic-password -s prwatch-github-packages\" ] || exit 2\n" +
            "[ -f '" + keychain + "/user' ] || exit 44\n" +
            "if [ \"${4:-}\" = \"-w\" ]; then [ -f '" + keychain + "/deny' ] && exit 51; cat '" + keychain + "/token'; exit 0; fi\n" +
            "printf 'keychain: \"/Users/x/Library/Keychains/login.keychain-db\"\\nclass: \"genp\"\\nattributes:\\n'\n" +
            "printf '    \"acct\"<blob>=\"%s\"\\n' \"$(cat '" + keychain + "/user')\"\n" +
            "printf '    \"svce\"<blob>=\"prwatch-github-packages\"\\n'\n");
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void Script_IsExecutable_AndNotARoomKitCopy()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(Executable, File.GetUnixFileMode(RepoFiles.PathOf("scripts/restore.sh")));
        Assert.DoesNotContain("roomkit:app-begin", RepoFiles.Read("scripts/restore.sh"), StringComparison.Ordinal);
    }

    [Fact]
    public void Keychain_SetsTheCredentialOnlyForOneDotnetRestore_InTheRepoRoot_AndPassesItsExitCode()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        WriteKeychain(User, Token);
        File.WriteAllText(Path.Combine(folder, "dotnet-exit"), "3\n");

        (int code, string stdout, string stderr) = RunScript(["-r", "osx-arm64"]);

        Assert.Equal(3, code);
        Assert.Equal(
            [
                "argv: restore -r osx-arm64",
                "cwd: repo root",
                "cred: Username=" + User + ";Password=" + Token,
            ],
            File.ReadAllLines(dotnetLog));
        Assert.DoesNotContain(Token, stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void File_IsUsed_WhenTheKeychainHasNoItem()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        WriteTokenFile(User + ":" + Token + "\n", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        (int code, string stdout, string stderr) = RunScript([]);

        Assert.True(code == 0, stderr);
        Assert.Contains("cred: Username=" + User + ";Password=" + Token, File.ReadAllLines(dotnetLog));
        Assert.Contains("argv: restore", File.ReadAllLines(dotnetLog));
        Assert.DoesNotContain(Token, stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTokenAnywhere_FailsWithoutRunningDotnet()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        (int code, _, string stderr) = RunScript([]);

        Assert.Equal(1, code);
        Assert.Contains("No GitHub Packages token", stderr, StringComparison.Ordinal);
        Assert.Contains("security add-generic-password -s prwatch-github-packages -a <user> -T '' -w", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(dotnetLog));
    }

    [Fact]
    public void DeniedKeychainPrompt_FailsWithoutRunningDotnet_AndDoesNotFallBackToTheFile()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        WriteKeychain(User, Token);
        File.WriteAllText(Path.Combine(keychain, "deny"), "");
        WriteTokenFile(User + ":" + Token + "\n", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        (int code, string stdout, string stderr) = RunScript([]);

        Assert.Equal(1, code);
        Assert.Contains("The keychain did not hand out the token (prompt denied?).", stderr, StringComparison.Ordinal);
        Assert.True(!File.Exists(dotnetLog) || File.ReadAllText(dotnetLog).Length == 0);
        Assert.DoesNotContain(Token, stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_ShowsTheAddCommandWithAPrompt_AndWarnsAboutTheFile()
    {
        string text = RepoFiles.Read("scripts/restore.sh");
        string[] lines = text.Split('\n');
        string[] header = lines.TakeWhile(line => line.StartsWith('#')).ToArray();

        Assert.Contains(header, line => line.Contains("security add-generic-password -s prwatch-github-packages -a <user> -T '' -w", StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains("No GitHub Packages token", StringComparison.Ordinal)
                && line.Contains("security add-generic-password -s $SERVICE -a <user> -T '' -w", StringComparison.Ordinal));
        Assert.All(
            lines.Where(line => line.Contains("add-generic-password", StringComparison.Ordinal)),
            line => Assert.Contains("-T ''", line, StringComparison.Ordinal));
        Assert.Contains(header, line => line.Contains("0600 file", StringComparison.Ordinal) && line.Contains("any code", StringComparison.Ordinal));
        Assert.Contains(header, line => line.Contains("did not hand it out", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("group-readable")]
    [InlineData("symlink")]
    [InlineData("no-colon")]
    [InlineData("two-lines")]
    public void TokenFile_Refused_FailsWithoutRunningDotnet(string scene)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        UnixFileMode privateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        switch (scene)
        {
            case "group-readable":
                WriteTokenFile(User + ":" + Token + "\n", privateFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                break;
            case "symlink":
                string target = Path.Combine(folder, "elsewhere");
                File.WriteAllText(target, User + ":" + Token + "\n");
                File.SetUnixFileMode(target, privateFile);
                File.CreateSymbolicLink(tokenFile, target);
                break;
            case "no-colon":
                WriteTokenFile(Token + "\n", privateFile);
                break;
            default:
                WriteTokenFile(User + ":" + Token + "\nsecond line\n", privateFile);
                break;
        }

        (int code, string stdout, string stderr) = RunScript([]);

        Assert.Equal(2, code);
        Assert.False(File.Exists(dotnetLog));
        Assert.DoesNotContain(Token, stdout + stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(User, "ghp_a;Password=b")]
    [InlineData(User, "")]
    [InlineData("octo user", Token)]
    [InlineData("octo;Password=x", Token)]
    [InlineData("", Token)]
    public void RefusedValues_FailWithoutRunningDotnet(string user, string token)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        WriteKeychain(user, token);

        (int code, string stdout, string stderr) = RunScript([]);

        Assert.Equal(2, code);
        Assert.False(File.Exists(dotnetLog));
        if (token.Length > 0)
        {
            Assert.DoesNotContain(token, stdout + stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GloballyExportedVariable_IsWarnedAbout_AndTheStoredTokenIsUsed()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        WriteKeychain(User, Token);

        (int code, _, string stderr) = RunScript([], new() { ["NuGetPackageSourceCredentials_github"] = "Username=old;Password=old" });

        Assert.True(code == 0, stderr);
        Assert.Contains("remove it from your shell profile", stderr, StringComparison.Ordinal);
        Assert.Contains("cred: Username=" + User + ";Password=" + Token, File.ReadAllLines(dotnetLog));
    }

    private void WriteKeychain(string user, string token)
    {
        File.WriteAllText(Path.Combine(keychain, "user"), user);
        File.WriteAllText(Path.Combine(keychain, "token"), token + "\n");
    }

    [SupportedOSPlatform("macos")]
    private void WriteTokenFile(string text, UnixFileMode mode)
    {
        File.WriteAllText(tokenFile, text);
        File.SetUnixFileMode(tokenFile, mode);
    }

    private void WriteFake(string name, string body)
    {
        string path = Path.Combine(bin, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private (int Code, string Stdout, string Stderr) RunScript(string[] args, Dictionary<string, string>? extra = null)
    {
        ProcessStartInfo start = new("/bin/sh")
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(repo, "scripts", "restore.sh"));
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Clear();
        start.Environment["PATH"] = bin + ":/usr/bin:/bin";
        start.Environment["HOME"] = home;
        foreach (KeyValuePair<string, string> pair in extra ?? [])
        {
            start.Environment[pair.Key] = pair.Value;
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        if (!process.WaitForExit(ProcessLimit))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("restore.sh did not exit within " + ProcessLimit.TotalSeconds + " s");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
