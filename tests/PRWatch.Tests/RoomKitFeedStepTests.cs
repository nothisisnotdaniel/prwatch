using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PRWatch.Tests;

/// <summary>Runs CI's "Fill the RoomKit feed" step with <c>/bin/bash -e</c> in its own temp folder, with a fake
/// <c>gh</c> (no network) and only <c>PATH</c>, <c>HOME</c>, <c>RUNNER_TEMP</c> and <c>GH_TOKEN</c> in its
/// environment.</summary>
public sealed class RoomKitFeedStepTests : IDisposable
{
    private const string Version = "1.2.3";
    private const string Package = "RoomKit." + Version + ".nupkg";
    private static readonly TimeSpan ProcessLimit = TimeSpan.FromSeconds(10);
    private static readonly byte[] PackageBytes = Encoding.UTF8.GetBytes("fake RoomKit package bytes");
    private static readonly string PackageHash = Convert.ToHexStringLower(SHA256.HashData(PackageBytes));
    private static readonly string OtherHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("other")));

    private readonly string folder = Directory.CreateTempSubdirectory("prwatch-feed-").FullName;
    private readonly string work;
    private readonly string fixtures;
    private readonly string calls;
    private readonly string attestExit;
    private readonly string feed;
    private readonly string script;

    public RoomKitFeedStepTests()
    {
        work = Path.Combine(folder, "work");
        fixtures = Path.Combine(folder, "fixtures");
        calls = Path.Combine(folder, "gh-calls");
        attestExit = Path.Combine(folder, "attest-exit");
        feed = Path.Combine(folder, "home", "Library", "Application Support", "RoomKit", "feed");
        Directory.CreateDirectory(Path.Combine(work, "src", "PRWatch"));
        Directory.CreateDirectory(fixtures);
        Directory.CreateDirectory(Path.Combine(folder, "home"));
        Directory.CreateDirectory(Path.Combine(folder, "runner"));
        File.WriteAllText(
            Path.Combine(work, "src", "PRWatch", "PRWatch.csproj"),
            "<Project>\n  <ItemGroup>\n    <PackageReference Include=\"RoomKit\" Version=\"[" + Version + "]\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllBytes(Path.Combine(fixtures, Package), PackageBytes);
        File.WriteAllText(attestExit, "0\n");
        script = Path.Combine(folder, "step.sh");
        File.WriteAllText(script, RunScript(Step(RepoFiles.Read(".github/workflows/ci.yml"), "Fill the RoomKit feed")));
        WriteFakeGh();
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void AllChecksPass_PutsThePackageInTheFeed()
    {
        WriteSums(PackageHash);
        WritePin(PackageHash + "  " + Package + "\n");

        (int code, _, string stderr) = RunStep();

        Assert.True(code == 0, stderr);
        Assert.Equal(PackageBytes, File.ReadAllBytes(Path.Combine(feed, Package)));
        string[] log = File.ReadAllLines(calls);
        Assert.Equal(2, log.Length);
        Assert.StartsWith("release download v" + Version + " --repo nothisisnotdaniel/controlroom ", log[0], StringComparison.Ordinal);
        Assert.Equal(
            "attestation verify " + Path.Combine(folder, "runner", "roomkit", Package) +
                " --repo nothisisnotdaniel/controlroom --signer-workflow nothisisnotdaniel/controlroom/.github/workflows/release.yml" +
                " --source-ref refs/heads/main --deny-self-hosted-runners",
            log[1]);
    }

    [Fact]
    public void PinMismatch_FailsBeforeTheAttestation_WithAnEmptyFeed()
    {
        WriteSums(PackageHash);
        WritePin(OtherHash + "  " + Package + "\n");

        (int code, _, string stderr) = RunStep();

        Assert.NotEqual(0, code);
        Assert.Contains(Package + " does not match roomkit.sha256.", stderr, StringComparison.Ordinal);
        AssertFeedEmpty();
        Assert.DoesNotContain("attestation", File.ReadAllText(calls), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("other-version")]
    [InlineData("two-lines")]
    [InlineData("upper-case")]
    public void PinMissingEmptyOrForAnotherVersion_Fails_WithAnEmptyFeed(string scene)
    {
        WriteSums(PackageHash);
        string? pin = scene switch
        {
            "missing" => null,
            "empty" => "",
            "other-version" => PackageHash + "  RoomKit.1.2.4.nupkg\n",
            "two-lines" => PackageHash + "  " + Package + "\n" + PackageHash + "  " + Package + "\n",
            _ => PackageHash.ToUpperInvariant() + "  " + Package + "\n",
        };
        if (pin is not null)
        {
            WritePin(pin);
        }

        (int code, _, string stderr) = RunStep();

        Assert.NotEqual(0, code);
        Assert.Contains(Package + " does not match roomkit.sha256.", stderr, StringComparison.Ordinal);
        AssertFeedEmpty();
        Assert.DoesNotContain("attestation", File.ReadAllText(calls), StringComparison.Ordinal);
    }

    [Fact]
    public void AttestationFails_FailsWithAnEmptyFeed()
    {
        WriteSums(PackageHash);
        WritePin(PackageHash + "  " + Package + "\n");
        File.WriteAllText(attestExit, "1\n");

        (int code, _, string stderr) = RunStep();

        Assert.NotEqual(0, code);
        Assert.Contains(Package + " has no valid ControlRoom release attestation.", stderr, StringComparison.Ordinal);
        AssertFeedEmpty();
        Assert.Contains("attestation verify", File.ReadAllText(calls), StringComparison.Ordinal);
    }

    [Fact]
    public void SumsMismatch_FailsAsBefore_WithAnEmptyFeed()
    {
        WriteSums(OtherHash);
        WritePin(PackageHash + "  " + Package + "\n");

        (int code, _, string stderr) = RunStep();

        Assert.NotEqual(0, code);
        Assert.Contains(Package + " does not match SHA256SUMS.", stderr, StringComparison.Ordinal);
        AssertFeedEmpty();
        Assert.DoesNotContain("attestation", File.ReadAllText(calls), StringComparison.Ordinal);
    }

    private void AssertFeedEmpty()
    {
        Assert.Empty(Directory.Exists(feed) ? Directory.GetFiles(feed, "*.nupkg") : []);
    }

    private void WriteSums(string hash)
    {
        File.WriteAllText(Path.Combine(fixtures, "SHA256SUMS"), hash + "  " + Package + "\n");
    }

    private void WritePin(string text)
    {
        File.WriteAllText(Path.Combine(work, "roomkit.sha256"), text);
    }

    private void WriteFakeGh()
    {
        string bin = Path.Combine(folder, "fakebin");
        Directory.CreateDirectory(bin);
        string gh = Path.Combine(bin, "gh");
        File.WriteAllText(
            gh,
            "#!/bin/sh\n" +
            "printf '%s\\n' \"$*\" >> '" + calls + "'\n" +
            "case \"$1 $2\" in\n" +
            "  \"release download\")\n" +
            "    dir=\"\"\n" +
            "    while [ $# -gt 0 ]; do\n" +
            "      if [ \"$1\" = \"--dir\" ]; then dir=\"$2\"; fi\n" +
            "      shift\n" +
            "    done\n" +
            "    cp '" + fixtures + "'/* \"$dir/\"\n" +
            "    exit 0 ;;\n" +
            "  \"attestation verify\")\n" +
            "    exit \"$(cat '" + attestExit + "')\" ;;\n" +
            "esac\n" +
            "exit 2\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>One step: from its <c>- name:</c> line to the next step or job, without trailing blank lines.</summary>
    private static string Step(string text, string name)
    {
        string[] lines = text.Split('\n');
        int start = Array.IndexOf(lines, "      - name: " + name);
        Assert.True(start >= 0, name);
        int end = start + 1;
        while (end < lines.Length && !lines[end].StartsWith("      - name: ", StringComparison.Ordinal) &&
            !(lines[end].Length > 2 && lines[end].StartsWith("  ", StringComparison.Ordinal) && lines[end][2] != ' '))
        {
            end++;
        }

        return string.Join('\n', lines[start..end]).TrimEnd() + "\n";
    }

    /// <summary>The script of a step's <c>run: |</c> block, its common indent removed.</summary>
    private static string RunScript(string step)
    {
        string[] lines = step.Split('\n');
        int run = Array.FindIndex(lines, line => line.TrimStart() == "run: |");
        Assert.True(run >= 0);
        int indent = lines[run].Length - lines[run].TrimStart().Length;
        string[] body = [.. lines.Skip(run + 1).TakeWhile(line => line.Trim().Length == 0 || line.Length - line.TrimStart().Length > indent)];
        int common = body.Where(line => line.Trim().Length > 0).Min(line => line.Length - line.TrimStart().Length);
        return string.Join('\n', body.Select(line => line.Length >= common ? line[common..] : "")).TrimEnd() + "\n";
    }

    private (int Code, string Stdout, string Stderr) RunStep()
    {
        ProcessStartInfo start = new("/bin/bash")
        {
            WorkingDirectory = work,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        start.Environment.Clear();
        start.Environment["PATH"] = Path.Combine(folder, "fakebin") + ":/usr/bin:/bin";
        start.Environment["HOME"] = Path.Combine(folder, "home");
        start.Environment["RUNNER_TEMP"] = Path.Combine(folder, "runner");
        start.Environment["GH_TOKEN"] = "x";

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        if (!process.WaitForExit(ProcessLimit))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the feed step did not exit within " + ProcessLimit.TotalSeconds + " s");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
