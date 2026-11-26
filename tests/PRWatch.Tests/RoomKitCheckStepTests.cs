using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PRWatch.Tests;

/// <summary>Runs CI's "Check the RoomKit package" step with <c>/bin/bash -e</c> in its own temp folder, with a fake
/// <c>gh</c> (no network) and only <c>PATH</c>, <c>HOME</c>, <c>NUGET_PACKAGES</c> and <c>GH_TOKEN</c> in its
/// environment, plus the step's jq filter run by the real <c>/usr/bin/jq</c> on a recorded <c>gh</c> result.</summary>
public sealed class RoomKitCheckStepTests : IDisposable
{
    private const string Version = "1.2.3";
    private const string Package = "RoomKit." + Version + ".nupkg";
    private const string Jq = "/usr/bin/jq";

    private const string Filter =
        "length > 0 and all(.[]; .verificationResult.signature.certificate | " +
        ".githubWorkflowTrigger == \"pull_request\" and .sourceRepositoryRef == \"refs/heads/main\" and " +
        ".runnerEnvironment == \"github-hosted\")";

    private const string RealResult = "tests/PRWatch.Tests/Fixtures/roomkit-0.1.0-attestation.json";
    private const string EndOfCall = "--end-of-call--";
    private static readonly TimeSpan ProcessLimit = TimeSpan.FromSeconds(10);
    private static readonly byte[] PackageBytes = Encoding.UTF8.GetBytes("fake RoomKit package bytes");
    private static readonly string PackageHash = Convert.ToHexStringLower(SHA256.HashData(PackageBytes));
    private static readonly string OtherHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("other")));

    private readonly string folder = Directory.CreateTempSubdirectory("prwatch-check-").FullName;
    private readonly string work;
    private readonly string packages;
    private readonly string home;
    private readonly string calls;
    private readonly string ghExit;
    private readonly string ghOutput;
    private readonly string script;

    public RoomKitCheckStepTests()
    {
        work = Path.Combine(folder, "work");
        packages = Path.Combine(folder, "packages");
        home = Path.Combine(folder, "home");
        calls = Path.Combine(folder, "gh-calls");
        ghExit = Path.Combine(folder, "gh-exit");
        ghOutput = Path.Combine(folder, "gh-output");
        Directory.CreateDirectory(Path.Combine(work, "src", "PRWatch"));
        Directory.CreateDirectory(home);
        File.WriteAllText(
            Path.Combine(work, "src", "PRWatch", "PRWatch.csproj"),
            "<Project>\n  <ItemGroup>\n    <PackageReference Include=\"RoomKit\" Version=\"[" + Version + "]\" />\n  </ItemGroup>\n</Project>\n");
        WritePackage(packages);
        File.WriteAllText(ghExit, "0\n");
        File.WriteAllText(ghOutput, "true\n");
        script = Path.Combine(folder, "step.sh");
        File.WriteAllText(script, RunScript(Step(RepoFiles.Read(".github/workflows/ci.yml"), "Check the RoomKit package")));
        WriteFakeGh();
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void AllChecksPass_CallsGhOnceWithTheExactArguments()
    {
        WritePin(PackageHash + "  " + Package + "\n");

        (int code, _, string stderr) = RunStep();

        Assert.True(code == 0, stderr);
        List<string[]> log = Calls();
        string[] argv = Assert.Single(log);
        Assert.Equal(
            [
                "attestation", "verify", PackagePath(packages),
                "--repo", "nothisisnotdaniel/roomkit",
                "--cert-identity", "https://github.com/nothisisnotdaniel/roomkit/.github/workflows/release.yml@refs/heads/main",
                "--cert-oidc-issuer", "https://token.actions.githubusercontent.com",
                "--source-ref", "refs/heads/main",
                "--deny-self-hosted-runners",
                "--format", "json",
                "--jq", Filter,
            ],
            argv);
    }

    [Fact]
    public void WithoutNugetPackages_HashesTheFileUnderHome()
    {
        WritePin(PackageHash + "  " + Package + "\n");
        string fallback = Path.Combine(home, ".nuget", "packages");
        WritePackage(fallback);

        (int code, _, string stderr) = RunStep(withNugetPackages: false);

        Assert.True(code == 0, stderr);
        Assert.Equal(PackagePath(fallback), Assert.Single(Calls())[2]);
    }

    [Fact]
    public void PinMismatch_FailsWithoutCallingGh()
    {
        WritePin(OtherHash + "  " + Package + "\n");

        (int code, _, string stderr) = RunStep();

        Assert.NotEqual(0, code);
        Assert.Contains(Package + " does not match roomkit.sha256.", stderr, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("other-version")]
    [InlineData("two-lines")]
    [InlineData("upper-case")]
    public void PinMissingEmptyOrForAnotherVersion_FailsWithoutCallingGh(string scene)
    {
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
        Assert.Empty(Calls());
    }

    [Fact]
    public void MissingVersion_FailsWithTheCurrentMessage()
    {
        WritePin(PackageHash + "  " + Package + "\n");
        File.WriteAllText(Path.Combine(work, "src", "PRWatch", "PRWatch.csproj"), "<Project />\n");

        (int code, _, string stderr) = RunStep();

        Assert.NotEqual(0, code);
        Assert.Contains("No pinned RoomKit version in src/PRWatch/PRWatch.csproj.", stderr, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    [Fact]
    public void PackageFileMissing_FailsWithoutCallingGh()
    {
        WritePin(PackageHash + "  " + Package + "\n");
        File.Delete(PackagePath(packages));

        (int code, _, string stderr) = RunStep();

        Assert.NotEqual(0, code);
        Assert.Contains(Package + " is not in the restored packages.", stderr, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    [Theory]
    [InlineData(1, "true\n")]
    [InlineData(0, "false\n")]
    [InlineData(0, "")]
    public void AttestationFailsOrIsNotExactlyTrue_Fails(int exit, string output)
    {
        WritePin(PackageHash + "  " + Package + "\n");
        File.WriteAllText(ghExit, exit + "\n");
        File.WriteAllText(ghOutput, output);

        (int code, _, string stderr) = RunStep();

        Assert.NotEqual(0, code);
        Assert.Contains(Package + " has no valid roomkit release attestation.", stderr, StringComparison.Ordinal);
        Assert.Single(Calls());
    }

    [JqTheory]
    [InlineData("real", "true")]
    [InlineData("push", "false")]
    [InlineData("none", "false")]
    public void Filter_IsTrueOnlyForAMergedPrRunOnMain(string scene, string expected)
    {
        WritePin(PackageHash + "  " + Package + "\n");
        Assert.Equal(0, RunStep().Code);
        string filter = Assert.Single(Calls())[^1];
        Assert.Equal(Filter, filter);
        string real = RepoFiles.Read(RealResult);
        Assert.Contains("\"githubWorkflowTrigger\":\"pull_request\"", real, StringComparison.Ordinal);
        string input = scene switch
        {
            "real" => real,
            "push" => real.Replace("\"githubWorkflowTrigger\":\"pull_request\"", "\"githubWorkflowTrigger\":\"push\"", StringComparison.Ordinal),
            _ => "[]\n",
        };

        (int code, string stdout, string stderr) = Run(Jq, [filter], input);

        Assert.True(code == 0, stderr);
        Assert.Equal(expected + "\n", stdout);
    }

    /// <summary>A theory that is skipped, with the reason, where <c>/usr/bin/jq</c> is not installed.</summary>
    private sealed class JqTheoryAttribute : TheoryAttribute
    {
        public JqTheoryAttribute()
        {
            if (!File.Exists(Jq))
            {
                Skip = Jq + " is not installed, so the step's jq filter cannot be run here.";
            }
        }
    }

    private static string PackagePath(string root)
    {
        return Path.Combine(root, "roomkit", Version, "roomkit." + Version + ".nupkg");
    }

    private static void WritePackage(string root)
    {
        string path = PackagePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PackageBytes);
    }

    private void WritePin(string text)
    {
        File.WriteAllText(Path.Combine(work, "roomkit.sha256"), text);
    }

    /// <summary>Every <c>gh</c> call, one argv each.</summary>
    private List<string[]> Calls()
    {
        List<string[]> result = [];
        if (!File.Exists(calls))
        {
            return result;
        }

        List<string> current = [];
        foreach (string line in File.ReadAllLines(calls))
        {
            if (line == EndOfCall)
            {
                result.Add([.. current]);
                current.Clear();
            }
            else
            {
                current.Add(line);
            }
        }

        return result;
    }

    private void WriteFakeGh()
    {
        string bin = Path.Combine(folder, "fakebin");
        Directory.CreateDirectory(bin);
        string gh = Path.Combine(bin, "gh");
        File.WriteAllText(
            gh,
            "#!/bin/sh\n" +
            "printf '%s\\n' \"$@\" '" + EndOfCall + "' >> '" + calls + "'\n" +
            "cat '" + ghOutput + "'\n" +
            "exit \"$(cat '" + ghExit + "')\"\n");
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

    private (int Code, string Stdout, string Stderr) RunStep(bool withNugetPackages = true)
    {
        Dictionary<string, string> env = new()
        {
            ["PATH"] = Path.Combine(folder, "fakebin") + ":/usr/bin:/bin",
            ["HOME"] = home,
            ["GH_TOKEN"] = "x",
        };
        if (withNugetPackages)
        {
            env["NUGET_PACKAGES"] = packages;
        }

        return Run("/bin/bash", ["-e", script], "", env);
    }

    private (int Code, string Stdout, string Stderr) Run(
        string program, string[] args, string stdin, Dictionary<string, string>? env = null)
    {
        ProcessStartInfo start = new(program)
        {
            WorkingDirectory = work,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";
        foreach (KeyValuePair<string, string> pair in env ?? [])
        {
            start.Environment[pair.Key] = pair.Value;
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        if (!process.WaitForExit(ProcessLimit))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail(program + " did not exit within " + ProcessLimit.TotalSeconds + " s");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
