using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PRWatch.Tests;

/// <summary><c>.github/workflows/release.yml</c> read as text, plus runs of
/// <c>.github/scripts/release-version.sh</c> and of the release job's check and create steps, each in its own
/// temp folder with only <c>PATH=/usr/bin:/bin</c> (and fakes) in its environment.</summary>
public sealed class ReleaseWorkflowTests : IDisposable
{
    private const string WorkflowPath = ".github/workflows/release.yml";
    private const string ScriptPath = ".github/scripts/release-version.sh";
    private const string Target = "0123456789abcdef0123456789abcdef01234567";
    private static readonly TimeSpan ProcessLimit = TimeSpan.FromSeconds(10);

    private readonly string folder = Directory.CreateTempSubdirectory("prwatch-release-").FullName;

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void Trigger_IsOnlyAMergedPrClosedIntoMain()
    {
        string workflow = Workflow();

        Assert.Contains(
            "\non:\n  pull_request:\n    types:\n      - \"closed\"\n    branches:\n      - \"main\"\n\npermissions: {}\n",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\n  push:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow_dispatch", workflow, StringComparison.Ordinal);
        Assert.Equal(["version", "macos-tests", "macos", "verify", "release"], JobNames(workflow));
    }

    [Fact]
    public void Permissions_OnlyReleaseCanWrite_AndOnlyTheBuildJobsReadPackages()
    {
        string workflow = Workflow();

        Assert.Equal(["contents: read"], Permissions(Job(workflow, "version")));
        Assert.Equal(["contents: read", "packages: read"], Permissions(Job(workflow, "macos-tests")));
        Assert.Equal(["contents: read", "packages: read"], Permissions(Job(workflow, "macos")));
        Assert.Equal(["contents: read", "packages: read"], Permissions(Job(workflow, "verify")));
        Assert.Equal(
            ["contents: write", "id-token: write", "attestations: write"], Permissions(Job(workflow, "release")));
        int permissionLines = workflow.Split('\n').Count(line => line.TrimStart().StartsWith("permissions:", StringComparison.Ordinal));
        Assert.Equal(6, permissionLines);
        foreach (string job in new[] { "version", "macos-tests", "macos", "verify" })
        {
            Assert.DoesNotContain("write", string.Join('\n', Permissions(Job(workflow, job))), StringComparison.Ordinal);
        }

        Assert.Equal(3, Count(workflow, "      packages: "));
        Assert.DoesNotContain("packages: write", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_IfAndCheckStep_RequireAMergedDevelopPrOfThisRepoOnArm64()
    {
        string version = Job(Workflow(), "version");

        Assert.Contains(
            "    if: github.event.pull_request.merged == true && github.event.pull_request.head.ref == 'develop' && github.event.pull_request.head.repo.full_name == github.repository\n",
            version,
            StringComparison.Ordinal);
        string checkout = Step(version, "Check out");
        Assert.Contains("persist-credentials: false", checkout, StringComparison.Ordinal);
        Assert.Contains("fetch-depth: 0", checkout, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ github.event.pull_request.merge_commit_sha }}", checkout, StringComparison.Ordinal);
        Assert.Contains("MERGE_SHA: ${{ github.event.pull_request.merge_commit_sha }}", version, StringComparison.Ordinal);

        Assert.Equal("Check the event", StepNames(version)[1]);
        string check = Step(version, "Check the event");
        string[] conditions =
        [
            "if [ \"$(uname -m)\" != \"arm64\" ]; then",
            "if [ \"$PR_MERGED\" != \"true\" ]; then",
            "if [ \"$PR_HEAD_REF\" != \"develop\" ]; then",
            "if [ \"$PR_HEAD_REPO\" != \"$GITHUB_REPOSITORY\" ]; then",
            "'' | *[!0-9a-f]*)",
            "if [ \"${#MERGE_SHA}\" -ne 40 ]; then",
            "if [ \"$(git rev-parse HEAD)\" != \"$MERGE_SHA\" ]; then",
            "if ! git merge-base --is-ancestor \"$MERGE_SHA\" origin/main; then",
        ];
        foreach (string condition in conditions)
        {
            int at = check.IndexOf(condition, StringComparison.Ordinal);
            Assert.True(at >= 0, condition);
            int exit = check.IndexOf("exit 1", at, StringComparison.Ordinal);
            int end = check.IndexOf(condition.StartsWith("if", StringComparison.Ordinal) ? "fi\n" : ";;", at, StringComparison.Ordinal);
            Assert.True(exit > at && exit < end, condition);
        }
    }

    [Fact]
    public void Version_CallsTheScriptWithTheTitleFromEnv_AndGivesBothOutputs()
    {
        string version = Job(Workflow(), "version");

        Assert.Contains("      PR_TITLE: ${{ github.event.pull_request.title }}\n", version, StringComparison.Ordinal);
        string step = Step(version, "Version");
        Assert.Contains("id: version", step, StringComparison.Ordinal);
        Assert.Contains(
            "if ! RELEASE_VERSION=\"$(git tag --list 'v*' | sh .github/scripts/release-version.sh \"$PR_TITLE\")\"; then",
            step,
            StringComparison.Ordinal);
        Assert.Contains("RELEASE_TARGET=\"$MERGE_SHA\"", step, StringComparison.Ordinal);
        Assert.Contains("echo \"RELEASE_VERSION=$RELEASE_VERSION\" >> \"$GITHUB_OUTPUT\"", step, StringComparison.Ordinal);
        Assert.Contains("echo \"RELEASE_TARGET=$RELEASE_TARGET\" >> \"$GITHUB_OUTPUT\"", step, StringComparison.Ordinal);
        Assert.Contains("      release_version: ${{ steps.version.outputs.RELEASE_VERSION }}\n", version, StringComparison.Ordinal);
        Assert.Contains("      release_target: ${{ steps.version.outputs.RELEASE_TARGET }}\n", version, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_IsControlRoomsCopyWithOnlyTheNameChanged()
    {
        string script = RepoFiles.Read(ScriptPath);

        Assert.StartsWith("#!/bin/sh\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ControlRoom", script, StringComparison.Ordinal);
        Assert.Contains("# Prints the version of a PRWatch release PR from its title", script, StringComparison.Ordinal);
        Assert.Contains("PRWatch makes no develop releases", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("release v0.1.0", "", 0, "0.1.0\n")]
    [InlineData("release v1.2.0-rc.1", "v1.1.0\n", 0, "1.2.0-rc.1\n")]
    [InlineData("release v0.1.1", "v0.1.0\nv0.0.9\n", 0, "0.1.1\n")]
    [InlineData("release v0.1.0", "v0.0.9\nv0.1.0\n", 1, "")]
    [InlineData("release v0.1.0", "v0.1.0", 1, "")]
    [InlineData("Release v0.1.0", "", 1, "")]
    [InlineData("release 0.1.0", "", 1, "")]
    [InlineData("release v0.1", "", 1, "")]
    [InlineData("release v01.0.0", "", 1, "")]
    [InlineData("release v0.1.0+build.1", "", 1, "")]
    [InlineData("release v0.1.0-develop.3", "", 1, "")]
    [InlineData(" release v0.1.0", "", 1, "")]
    [InlineData("release v0.1.0 ", "", 1, "")]
    [InlineData("release v0.1.0\n", "", 1, "")]
    [InlineData("release v0.1.0\nrelease v0.2.0", "", 1, "")]
    [InlineData("release v0.1.0\r", "", 1, "")]
    [InlineData("", "", 1, "")]
    public void Script_PrintsTheVersionOnlyForAnExactNewReleaseTitle(string title, string tags, int exit, string stdout)
    {
        string script = RepoFiles.PathOf(ScriptPath);

        (int code, string output, _) = Run(["/bin/sh", script, title], tags, []);

        Assert.Equal(exit, code);
        Assert.Equal(stdout, output);
    }

    [Fact]
    public void Script_WithoutATitleArgument_Refuses()
    {
        (int code, string output, _) = Run(["/bin/sh", RepoFiles.PathOf(ScriptPath)], "", []);

        Assert.Equal(1, code);
        Assert.Equal("", output);
    }

    [Fact]
    public void BuildJobs_RestoreThenCheckRoomKitLikeCi_BeforeAnyOtherDotnet()
    {
        string workflow = Workflow();
        string check = Step(RepoFiles.Read(".github/workflows/ci.yml"), "Check the RoomKit package");

        Assert.Contains("gh attestation verify \"$package\" --repo nothisisnotdaniel/roomkit", check, StringComparison.Ordinal);
        Assert.Equal(3, Count(workflow, check));
        foreach ((string name, string restoreRun) in new[]
        {
            ("macos-tests", "run: dotnet restore\n"),
            ("macos", "run: dotnet restore src/PRWatch -r osx-arm64\n"),
            ("verify", "run: dotnet restore src/PRWatch -r osx-arm64\n"),
        })
        {
            string job = Job(workflow, name);
            List<string> steps = StepNames(job);
            Assert.Equal(steps.IndexOf("Restore") + 1, steps.IndexOf("Check the RoomKit package"));
            Assert.Equal(
                "      - name: Restore\n        env:\n          " +
                    "NuGetPackageSourceCredentials_github: Username=github;Password=${{ secrets.GITHUB_TOKEN }}\n        " +
                    restoreRun,
                Step(job, "Restore"));
            Assert.Equal(check, Step(job, "Check the RoomKit package"));
            int restoreAt = job.IndexOf("      - name: Restore\n", StringComparison.Ordinal);
            int checkAt = job.IndexOf(check, StringComparison.Ordinal);
            List<string> dotnets = [.. RunBlocks(job).Where(run => run.StartsWith("run: dotnet ", StringComparison.Ordinal))];
            Assert.Equal(restoreRun.TrimEnd('\n'), dotnets[0]);
            int firstOther = job.IndexOf(dotnets[1], StringComparison.Ordinal);
            Assert.True(restoreAt > 0 && checkAt > restoreAt && firstOther > checkAt, name);
            foreach (string run in dotnets.Skip(1))
            {
                Assert.True(run.Contains(" --no-restore", StringComparison.Ordinal) || run.Contains(" --no-build", StringComparison.Ordinal), run);
            }

            Assert.Equal(1, Count(job, "NuGetPackageSourceCredentials_github: "));
            Assert.Equal(1, Count(job, "GH_TOKEN"));
            Assert.Contains("    runs-on: macos-15\n", job, StringComparison.Ordinal);
            string checkout = Step(job, StepNames(job)[0]);
            Assert.Contains("ref: ${{ needs.version.outputs.release_target }}", checkout, StringComparison.Ordinal);
            Assert.Contains("persist-credentials: false", checkout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Workflow_HasNoFolderFeedOrControlRoomRelease()
    {
        string workflow = Workflow();

        Assert.DoesNotContain("gh release download", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("nothisisnotdaniel/controlroom", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("RoomKit/feed", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_RunInOrder_TestsBuildThenTestBeforeThePublish()
    {
        string workflow = Workflow();
        string tests = Job(workflow, "macos-tests");

        Assert.Contains("    needs: version\n", tests, StringComparison.Ordinal);
        Assert.Contains("    needs: [version, macos-tests]\n", Job(workflow, "macos"), StringComparison.Ordinal);
        Assert.Contains("    needs: [version, macos]\n", Job(workflow, "verify"), StringComparison.Ordinal);
        Assert.Contains("    needs: [version, macos, verify]\n", Job(workflow, "release"), StringComparison.Ordinal);
        Assert.Equal(["run: dotnet restore", "run: dotnet build -warnaserror --no-restore", "run: dotnet test --no-build"], RunBlocks(tests).Where(run => run.StartsWith("run: dotnet", StringComparison.Ordinal)));
        Assert.True(
            tests.IndexOf("run: dotnet build -warnaserror --no-restore", StringComparison.Ordinal) <
            tests.IndexOf("run: dotnet test --no-build", StringComparison.Ordinal));
    }

    [Fact]
    public void Macos_PublishesSmokeChecksPacksAndHashes()
    {
        string macos = Job(Workflow(), "macos");

        Assert.Contains("      RELEASE_VERSION: ${{ needs.version.outputs.release_version }}\n", macos, StringComparison.Ordinal);
        Assert.Contains(
            "run: dotnet publish src/PRWatch -c Release -r osx-arm64 --self-contained --no-restore -p:Version=\"$RELEASE_VERSION\" -p:ContinuousIntegrationBuild=true -o \"$RUNNER_TEMP/publish\"\n",
            Step(macos, "Publish"),
            StringComparison.Ordinal);

        string smoke = Step(macos, "Smoke check the published app");
        Assert.Contains("ACTUAL=\"$(\"$RUNNER_TEMP/publish/PRWatch\" version)\"", smoke, StringComparison.Ordinal);
        Assert.Contains("if [ \"$ACTUAL\" != \"PRWatch $RELEASE_VERSION\" ]; then", smoke, StringComparison.Ordinal);
        Assert.Contains("exit 1", smoke, StringComparison.Ordinal);

        string pack = Step(macos, "Pack");
        Assert.Contains("COPYFILE_DISABLE: \"1\"", pack, StringComparison.Ordinal);
        string[] packLines =
        [
            "NAME=\"prwatch-$RELEASE_VERSION-osx-arm64\"",
            "mv \"$RUNNER_TEMP/publish\" \"$RUNNER_TEMP/pack/$NAME\"",
            "if [ ! -f \"$RUNNER_TEMP/pack/$NAME/wwwroot/index.html\" ]; then",
            "if [ -f scripts/updater.sh ]; then",
            "cp scripts/updater.sh \"$RUNNER_TEMP/pack/$NAME/updater.sh\"",
            "chmod 0755 \"$RUNNER_TEMP/pack/$NAME/updater.sh\"",
            "echo \"::warning::scripts/updater.sh is missing, so this tarball ships without the updater\"",
            "tar -czf \"dist/$NAME.tar.gz\" -C \"$RUNNER_TEMP/pack\" \"$NAME\"",
        ];
        AssertInOrder(pack, packLines);

        string hash = Step(macos, "Hash the tarball");
        Assert.Contains("id: hash", hash, StringComparison.Ordinal);
        Assert.Contains(
            "TARBALL_SHA256=\"$(shasum -a 256 \"dist/prwatch-$RELEASE_VERSION-osx-arm64.tar.gz\" | cut -d ' ' -f 1)\"",
            hash,
            StringComparison.Ordinal);
        Assert.Contains("echo \"tarball_sha256=$TARBALL_SHA256\" >> \"$GITHUB_OUTPUT\"", hash, StringComparison.Ordinal);
        Assert.Contains("      tarball_sha256: ${{ steps.hash.outputs.tarball_sha256 }}\n", macos, StringComparison.Ordinal);

        string save = Step(macos, "Save the tarball");
        Assert.Contains("name: macos-release", save, StringComparison.Ordinal);
        Assert.Contains("path: dist/prwatch-${{ env.RELEASE_VERSION }}-osx-arm64.tar.gz\n", save, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", save, StringComparison.Ordinal);
        AssertInOrder(macos, ["- name: Publish", "- name: Smoke check", "- name: Pack", "- name: Hash", "- name: Save"]);
    }

    [Fact]
    public void Pack_WithAndWithoutUpdater_MakesOneTopFolder()
    {
        string pack = RunScript(Step(Job(Workflow(), "macos"), "Pack"));
        string temp = Path.Combine(folder, "runner");
        string work = Path.Combine(folder, "work");
        Directory.CreateDirectory(Path.Combine(temp, "publish", "wwwroot"));
        File.WriteAllText(Path.Combine(temp, "publish", "wwwroot", "index.html"), "<!doctype html>\n");
        File.WriteAllText(Path.Combine(temp, "publish", "PRWatch"), "app\n");
        Directory.CreateDirectory(Path.Combine(work, "scripts"));
        string scriptFile = WriteScript(pack);
        Dictionary<string, string> env = new() { ["RELEASE_VERSION"] = "0.1.0", ["RUNNER_TEMP"] = temp, ["COPYFILE_DISABLE"] = "1" };

        (int code, string output, _) = Run(["/bin/bash", "-e", scriptFile], "", env, work);

        Assert.Equal(0, code);
        Assert.Contains("::warning::scripts/updater.sh is missing", output, StringComparison.Ordinal);
        (int listCode, string list, _) = Run(["/usr/bin/tar", "-tzf", Path.Combine(work, "dist", "prwatch-0.1.0-osx-arm64.tar.gz")], "", [], work);
        Assert.Equal(0, listCode);
        string[] entries = list.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(entries, entry => Assert.StartsWith("prwatch-0.1.0-osx-arm64/", entry, StringComparison.Ordinal));
        Assert.Contains("prwatch-0.1.0-osx-arm64/wwwroot/index.html", entries);
        Assert.DoesNotContain(entries, entry => entry.EndsWith("updater.sh", StringComparison.Ordinal));

        Directory.Delete(Path.Combine(work, "dist"), recursive: true);
        Directory.Delete(Path.Combine(temp, "pack"), recursive: true);
        Directory.CreateDirectory(Path.Combine(temp, "publish", "wwwroot"));
        File.WriteAllText(Path.Combine(temp, "publish", "wwwroot", "index.html"), "<!doctype html>\n");
        File.WriteAllText(Path.Combine(work, "scripts", "updater.sh"), "#!/bin/sh\n");

        (int withCode, string withOutput, _) = Run(["/bin/bash", "-e", scriptFile], "", env, work);

        Assert.Equal(0, withCode);
        Assert.DoesNotContain("::warning::", withOutput, StringComparison.Ordinal);
        string updater = Path.Combine(temp, "pack", "prwatch-0.1.0-osx-arm64", "updater.sh");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                File.GetUnixFileMode(updater));
        }

        (_, string withList, _) = Run(["/usr/bin/tar", "-tzf", Path.Combine(work, "dist", "prwatch-0.1.0-osx-arm64.tar.gz")], "", [], work);
        Assert.Contains("prwatch-0.1.0-osx-arm64/updater.sh", withList.Split('\n'));
    }

    [Fact]
    public void Pack_WithoutIndexHtml_Fails()
    {
        string pack = RunScript(Step(Job(Workflow(), "macos"), "Pack"));
        string temp = Path.Combine(folder, "runner");
        string work = Path.Combine(folder, "work");
        Directory.CreateDirectory(Path.Combine(temp, "publish"));
        Directory.CreateDirectory(work);
        Dictionary<string, string> env = new() { ["RELEASE_VERSION"] = "0.1.0", ["RUNNER_TEMP"] = temp };

        (int code, _, string error) = Run(["/bin/bash", "-e", WriteScript(pack)], "", env, work);

        Assert.Equal(1, code);
        Assert.Contains("no wwwroot/index.html", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(work, "dist", "prwatch-0.1.0-osx-arm64.tar.gz")));
    }

    [Fact]
    public void Pack_WithASymlinkedUpdater_Fails()
    {
        string pack = RunScript(Step(Job(Workflow(), "macos"), "Pack"));
        string temp = Path.Combine(folder, "runner");
        string work = Path.Combine(folder, "work");
        Directory.CreateDirectory(Path.Combine(temp, "publish", "wwwroot"));
        File.WriteAllText(Path.Combine(temp, "publish", "wwwroot", "index.html"), "<!doctype html>\n");
        Directory.CreateDirectory(Path.Combine(work, "scripts"));
        string real = Path.Combine(folder, "elsewhere.sh");
        File.WriteAllText(real, "#!/bin/sh\n");
        File.CreateSymbolicLink(Path.Combine(work, "scripts", "updater.sh"), real);
        Dictionary<string, string> env = new() { ["RELEASE_VERSION"] = "0.1.0", ["RUNNER_TEMP"] = temp };

        (int code, _, string error) = Run(["/bin/bash", "-e", WriteScript(pack)], "", env, work);

        Assert.Equal(1, code);
        Assert.Contains("scripts/updater.sh is a symlink", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(work, "dist", "prwatch-0.1.0-osx-arm64.tar.gz")));
    }

    [Fact]
    public void Release_ChecksThenSumsThenAttestsThenCreates()
    {
        string release = Job(Workflow(), "release");

        Assert.Contains("    runs-on: ubuntu-latest\n", release, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet", release, StringComparison.Ordinal);
        Assert.Contains("      RELEASE_VERSION: ${{ needs.version.outputs.release_version }}\n", release, StringComparison.Ordinal);
        Assert.Contains("      RELEASE_TARGET: ${{ needs.version.outputs.release_target }}\n", release, StringComparison.Ordinal);
        string download = Step(release, "Collect the build artifact");
        Assert.Contains("name: macos-release", download, StringComparison.Ordinal);
        Assert.Contains("path: dist", download, StringComparison.Ordinal);
        Assert.Contains("          TARBALL_SHA256: ${{ needs.macos.outputs.tarball_sha256 }}\n", Step(release, "Check the downloaded files"), StringComparison.Ordinal);
        Assert.Contains("          VERIFIED_SHA256: ${{ needs.verify.outputs.tarball_sha256 }}\n", Step(release, "Check the downloaded files"), StringComparison.Ordinal);
        Assert.Contains(
            "shasum -a 256 \"prwatch-$RELEASE_VERSION-osx-arm64.tar.gz\" > SHA256SUMS", Step(release, "Checksums"), StringComparison.Ordinal);
        Assert.Contains(
            "subject-path: dist/prwatch-${{ env.RELEASE_VERSION }}-osx-arm64.tar.gz", Step(release, "Attest build provenance"), StringComparison.Ordinal);
        string create = Step(release, "Create GitHub release");
        Assert.Contains("GH_TOKEN: ${{ github.token }}", create, StringComparison.Ordinal);
        Assert.Contains(
            "gh release create \"v$RELEASE_VERSION\" \"dist/prwatch-$RELEASE_VERSION-osx-arm64.tar.gz\" dist/SHA256SUMS --title \"PRWatch $RELEASE_VERSION\" --generate-notes --target \"$RELEASE_TARGET\" $PRERELEASE\n",
            create,
            StringComparison.Ordinal);
        Assert.Contains("*-*) PRERELEASE=\"--prerelease\" ;;", create, StringComparison.Ordinal);
        Assert.Equal(
            ["Collect the build artifact", "Check the downloaded files", "Checksums", "Attest build provenance", "Create GitHub release"],
            StepNames(release));

        // The write-scoped job has no checkout and runs no git; gh names the repository through GH_REPO.
        Assert.DoesNotContain("actions/checkout@", release, StringComparison.Ordinal);
        Assert.DoesNotContain("git ", release, StringComparison.Ordinal);
        Assert.Contains("          GH_REPO: ${{ github.repository }}\n", create, StringComparison.Ordinal);
        Assert.Contains("          GH_TOKEN: ${{ github.token }}\n", create, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("good", 0)]
    [InlineData("extra", 1)]
    [InlineData("hidden", 1)]
    [InlineData("symlink", 1)]
    [InlineData("missing", 1)]
    [InlineData("upper", 1)]
    [InlineData("short", 1)]
    [InlineData("empty", 1)]
    [InlineData("other", 1)]
    [InlineData("verified-upper", 1)]
    [InlineData("verified-short", 1)]
    [InlineData("verified-empty", 1)]
    [InlineData("verified-unset", 1)]
    [InlineData("verified-other", 1)]
    [InlineData("verified-long", 1)]
    public void Release_CheckStep_AcceptsOnlyTheReportedAndVerifiedTarball(string scene, int exit)
    {
        string check = RunScript(Step(Job(Workflow(), "release"), "Check the downloaded files"));
        string dist = Path.Combine(folder, "dist");
        Directory.CreateDirectory(dist);
        string tarball = Path.Combine(dist, "prwatch-0.1.0-osx-arm64.tar.gz");
        byte[] bytes = Encoding.UTF8.GetBytes("tarball bytes");
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (scene == "symlink")
        {
            string real = Path.Combine(folder, "real.tar.gz");
            File.WriteAllBytes(real, bytes);
            File.CreateSymbolicLink(tarball, real);
        }
        else if (scene != "missing")
        {
            File.WriteAllBytes(tarball, bytes);
        }

        if (scene == "extra")
        {
            File.WriteAllText(Path.Combine(dist, "SHA256SUMS"), "x\n");
        }
        else if (scene == "hidden")
        {
            File.WriteAllText(Path.Combine(dist, ".extra"), "x\n");
        }

        string reported = scene switch
        {
            "upper" => hash.ToUpperInvariant(),
            "short" => hash[..63],
            "empty" => "",
            "other" => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("other"))),
            _ => hash,
        };
        string verified = scene switch
        {
            "verified-upper" => hash.ToUpperInvariant(),
            "verified-short" => hash[..63],
            "verified-long" => hash + "0",
            "verified-empty" => "",
            "verified-other" => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("other"))),
            _ => hash,
        };
        Dictionary<string, string> env = new() { ["RELEASE_VERSION"] = "0.1.0", ["TARBALL_SHA256"] = reported };
        if (scene != "verified-unset")
        {
            env["VERIFIED_SHA256"] = verified;
        }

        (int code, _, _) = Run(["/bin/bash", "-e", WriteScript(check)], "", env);

        Assert.Equal(exit, code);
    }

    [Fact]
    public void Verify_RepeatsTheMacosBuildStepsByteForByte_ThenChecksHashesAndCompares()
    {
        string workflow = Workflow();
        string macos = Job(workflow, "macos");
        string verify = Job(workflow, "verify");

        Assert.Contains("    runs-on: macos-15\n", verify, StringComparison.Ordinal);
        Assert.Contains("      RELEASE_VERSION: ${{ needs.version.outputs.release_version }}\n", verify, StringComparison.Ordinal);
        foreach (string name in new[]
        {
            "Check out the release commit", "Set up .NET 10", "Restore", "Check the RoomKit package", "Publish",
            "Smoke check the published app", "Pack",
        })
        {
            Assert.Equal(Step(macos, name), Step(verify, name));
        }

        Assert.Contains("persist-credentials: false", Step(verify, "Check out the release commit"), StringComparison.Ordinal);
        Assert.Contains("ref: ${{ needs.version.outputs.release_target }}", Step(verify, "Check out the release commit"), StringComparison.Ordinal);
        Assert.Contains(" -p:ContinuousIntegrationBuild=true ", Step(macos, "Publish"), StringComparison.Ordinal);
        Assert.Contains(" -p:ContinuousIntegrationBuild=true ", Step(verify, "Publish"), StringComparison.Ordinal);

        Assert.Equal(
            [
                "Check out the release commit", "Set up .NET 10", "Restore", "Check the RoomKit package", "Publish",
                "Smoke check the published app", "Pack", "Collect the build artifact", "Check and hash the downloaded tarball",
                "Compare the tarballs",
            ],
            StepNames(verify));
        string download = Step(verify, "Collect the build artifact");
        Assert.Contains("uses: actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093 # v4.3.0\n", download, StringComparison.Ordinal);
        Assert.Contains("          name: macos-release\n", download, StringComparison.Ordinal);
        Assert.Contains("          path: downloaded\n", download, StringComparison.Ordinal);
        Assert.Contains("        id: check\n", Step(verify, "Check and hash the downloaded tarball"), StringComparison.Ordinal);
        Assert.Contains("      tarball_sha256: ${{ steps.check.outputs.tarball_sha256 }}\n", verify, StringComparison.Ordinal);
        Assert.Equal(
            "      - name: Compare the tarballs\n        run: python3 -I .github/scripts/compare-release.py " +
                "\"dist/prwatch-$RELEASE_VERSION-osx-arm64.tar.gz\" \"downloaded/prwatch-$RELEASE_VERSION-osx-arm64.tar.gz\"\n",
            Step(verify, "Compare the tarballs"));

        // Read-only: no upload, no release, no attestation and no token beyond Restore and the RoomKit check.
        Assert.DoesNotContain("upload-artifact", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("attest-build-provenance", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release", verify, StringComparison.Ordinal);
        Assert.Equal(1, Count(verify, "secrets."));
        Assert.Equal(1, Count(verify, "github.token"));
    }

    [Theory]
    [InlineData("good", 0)]
    [InlineData("extra", 1)]
    [InlineData("hidden", 1)]
    [InlineData("folder", 1)]
    [InlineData("symlink", 1)]
    [InlineData("missing", 1)]
    [InlineData("tarball-folder", 1)]
    public void Verify_CheckStep_HashesOnlyTheRegularFileTarball(string scene, int exit)
    {
        string check = RunScript(Step(Job(Workflow(), "verify"), "Check and hash the downloaded tarball"));
        string downloaded = Path.Combine(folder, "downloaded");
        Directory.CreateDirectory(downloaded);
        string tarball = Path.Combine(downloaded, "prwatch-0.1.0-osx-arm64.tar.gz");
        byte[] bytes = Encoding.UTF8.GetBytes("tarball bytes");
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        switch (scene)
        {
            case "symlink":
                string real = Path.Combine(folder, "real.tar.gz");
                File.WriteAllBytes(real, bytes);
                File.CreateSymbolicLink(tarball, real);
                break;
            case "tarball-folder":
                Directory.CreateDirectory(tarball);
                break;
            case "missing":
                break;
            default:
                File.WriteAllBytes(tarball, bytes);
                break;
        }

        if (scene == "extra")
        {
            File.WriteAllText(Path.Combine(downloaded, "SHA256SUMS"), "x\n");
        }
        else if (scene == "hidden")
        {
            File.WriteAllText(Path.Combine(downloaded, ".extra"), "x\n");
        }
        else if (scene == "folder")
        {
            Directory.CreateDirectory(Path.Combine(downloaded, "sub"));
        }

        string output = Path.Combine(folder, "github-output");
        File.WriteAllText(output, "");
        Dictionary<string, string> env = new() { ["RELEASE_VERSION"] = "0.1.0", ["GITHUB_OUTPUT"] = output };

        (int code, _, _) = Run(["/bin/bash", "-e", WriteScript(check)], "", env);

        Assert.Equal(exit, code);
        Assert.Equal(exit == 0 ? "tarball_sha256=" + hash + "\n" : "", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("0.1.0", "v0.1.0|dist/prwatch-0.1.0-osx-arm64.tar.gz|dist/SHA256SUMS|--title|PRWatch 0.1.0|--generate-notes|--target|" + Target + "|")]
    [InlineData("1.2.0-rc.1", "v1.2.0-rc.1|dist/prwatch-1.2.0-rc.1-osx-arm64.tar.gz|dist/SHA256SUMS|--title|PRWatch 1.2.0-rc.1|--generate-notes|--target|" + Target + "|--prerelease|")]
    public void Release_CreateStep_AddsPrereleaseOnlyForAHyphenVersion(string version, string expected)
    {
        string create = RunScript(Step(Job(Workflow(), "release"), "Create GitHub release"));
        string bin = Path.Combine(folder, "bin");
        Directory.CreateDirectory(bin);
        string log = Path.Combine(folder, "gh-args");
        string gh = Path.Combine(bin, "gh");
        File.WriteAllText(gh, "#!/bin/sh\nshift 2\nfor arg in \"$@\"; do printf '%s|' \"$arg\"; done > \"$GH_LOG\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Dictionary<string, string> env = new()
        {
            ["PATH"] = bin + ":/usr/bin:/bin",
            ["RELEASE_VERSION"] = version,
            ["RELEASE_TARGET"] = Target,
            ["GH_LOG"] = log,
        };

        (int code, _, _) = Run(["/bin/bash", "-e", WriteScript(create)], "", env);

        Assert.Equal(0, code);
        Assert.Equal(expected, File.ReadAllText(log));
    }

    [Fact]
    public void RunBlocks_HoldNoExpressions_AndActionsArePinned()
    {
        string workflow = Workflow();

        List<string> runs = RunBlocks(workflow);
        Assert.Equal(22, runs.Count);
        foreach (string run in runs)
        {
            Assert.DoesNotContain("${{", run, StringComparison.Ordinal);
        }

        string[] uses = [.. workflow.Split('\n')
            .Where(line => line.TrimStart().StartsWith("uses:", StringComparison.Ordinal) ||
                line.TrimStart().StartsWith("- uses:", StringComparison.Ordinal))];
        Assert.Equal(11, uses.Length);
        foreach (string line in uses)
        {
            Assert.Matches(@"uses: [\w.-]+/[\w.-]+@[0-9a-f]{40}( #.*)?$", line);
        }

        foreach (string pin in new[]
        {
            "actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4.4.0",
            "actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1",
            "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4.6.2",
            "actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093 # v4.3.0",
            "actions/attest-build-provenance@4d101475d8b20a2381f78447822ac1eab6504dd8 # v4.2.2",
        })
        {
            Assert.Contains(uses, line => line.EndsWith(pin, StringComparison.Ordinal));
        }

        // The only secret is the job token, on the three Restore steps.
        Assert.Equal(3, Count(workflow, "secrets."));
        Assert.Equal(3, Count(workflow, "Password=${{ secrets.GITHUB_TOKEN }}\n"));
        Assert.DoesNotContain("pull_request_target", workflow, StringComparison.Ordinal);
        int checkouts = Count(workflow, "uses: actions/checkout@");
        Assert.Equal(4, checkouts);
        Assert.Equal(checkouts, Count(workflow, "persist-credentials: false"));
    }

    private static string Workflow()
    {
        return RepoFiles.Read(WorkflowPath);
    }

    private static List<string> JobNames(string workflow)
    {
        string jobs = workflow[(workflow.IndexOf("\njobs:\n", StringComparison.Ordinal) + 7)..];
        return [.. jobs.Split('\n')
            .Where(line => line.Length > 2 && line.StartsWith("  ", StringComparison.Ordinal) && line[2] != ' ' && line[2] != '#')
            .Select(line => line.Trim().TrimEnd(':'))];
    }

    /// <summary>The lines of one job: from <c>  name:</c> to the next line indented two spaces.</summary>
    private static string Job(string workflow, string name)
    {
        string jobs = workflow[(workflow.IndexOf("\njobs:\n", StringComparison.Ordinal) + 7)..];
        string[] lines = jobs.Split('\n');
        int start = Array.IndexOf(lines, "  " + name + ":");
        Assert.True(start >= 0, name);
        int end = start + 1;
        while (end < lines.Length && !(lines[end].Length > 2 && lines[end].StartsWith("  ", StringComparison.Ordinal) && lines[end][2] != ' '))
        {
            end++;
        }

        return string.Join('\n', lines[start..end]) + "\n";
    }

    private static List<string> Permissions(string job)
    {
        string[] lines = job.Split('\n');
        int start = Array.IndexOf(lines, "    permissions:");
        Assert.True(start >= 0);
        return [.. lines.Skip(start + 1).TakeWhile(line => line.StartsWith("      ", StringComparison.Ordinal)).Select(line => line.Trim())];
    }

    private static List<string> StepNames(string job)
    {
        return [.. job.Split('\n')
            .Where(line => line.StartsWith("      - name: ", StringComparison.Ordinal))
            .Select(line => line["      - name: ".Length..])];
    }

    /// <summary>One step: from its <c>- name:</c> line to the next step, without trailing blank lines.</summary>
    private static string Step(string text, string name)
    {
        string[] lines = text.Split('\n');
        int start = Array.IndexOf(lines, "      - name: " + name);
        Assert.True(start >= 0, name);
        int end = start + 1;
        while (end < lines.Length && !lines[end].StartsWith("      - name: ", StringComparison.Ordinal) &&
            !lines[end].StartsWith("      # ", StringComparison.Ordinal) &&
            !(lines[end].Length > 2 && lines[end].StartsWith("  ", StringComparison.Ordinal) && lines[end][2] != ' '))
        {
            end++;
        }

        while (end > start && lines[end - 1].Trim().Length == 0)
        {
            end--;
        }

        return string.Join('\n', lines[start..end]) + "\n";
    }

    /// <summary>Every <c>run:</c> block: <c>run: &lt;command&gt;</c>, or <c>run: |</c> with the lines indented under it.</summary>
    private static List<string> RunBlocks(string text)
    {
        string[] lines = text.Split('\n');
        List<string> blocks = [];
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("run:", StringComparison.Ordinal))
            {
                continue;
            }

            int indent = lines[i].Length - trimmed.Length;
            StringBuilder block = new(trimmed);
            if (trimmed == "run: |")
            {
                int j = i + 1;
                while (j < lines.Length && (lines[j].Trim().Length == 0 || lines[j].Length - lines[j].TrimStart().Length > indent))
                {
                    block.Append('\n').Append(lines[j]);
                    j++;
                }
            }

            blocks.Add(block.ToString());
        }

        return blocks;
    }

    /// <summary>The script of a step's <c>run: |</c> block, its common indent removed.</summary>
    private static string RunScript(string step)
    {
        string block = RunBlocks(step).Single();
        Assert.StartsWith("run: |\n", block, StringComparison.Ordinal);
        string[] lines = block.Split('\n')[1..];
        int indent = lines.Where(line => line.Trim().Length > 0).Min(line => line.Length - line.TrimStart().Length);
        return string.Join('\n', lines.Select(line => line.Length >= indent ? line[indent..] : "")).TrimEnd() + "\n";
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        int at = text.IndexOf(part, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static void AssertInOrder(string text, string[] parts)
    {
        int last = -1;
        foreach (string part in parts)
        {
            int at = text.IndexOf(part, last + 1, StringComparison.Ordinal);
            Assert.True(at > last, part);
            last = at;
        }
    }

    private string WriteScript(string script)
    {
        string path = Path.Combine(folder, "step-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, script);
        return path;
    }

    private (int Code, string Stdout, string Stderr) Run(string[] argv, string stdin, Dictionary<string, string> env, string? cwd = null)
    {
        ProcessStartInfo start = new(argv[0])
        {
            WorkingDirectory = cwd ?? folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in argv[1..])
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";
        foreach (KeyValuePair<string, string> pair in env)
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
            Assert.Fail(argv[0] + " did not exit within " + ProcessLimit.TotalSeconds + " s");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
