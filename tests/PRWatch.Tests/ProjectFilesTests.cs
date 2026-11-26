using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PRWatch.Tests;

/// <summary>The repo's build files, <c>Program.cs</c>, the options, the docs and the CI workflow, read as text.</summary>
public sealed class ProjectFilesTests
{
    [Fact]
    public void NugetConfig_MapsRoomKitOnlyToGitHubPackages()
    {
        string text = RepoFiles.Read("nuget.config");
        XDocument config = XDocument.Parse(text);
        XElement sources = config.Root!.Element("packageSources")!;

        Assert.Equal("clear", sources.Elements().First().Name.LocalName);
        Dictionary<string, string> byKey = sources.Elements("add")
            .ToDictionary(add => (string)add.Attribute("key")!, add => (string)add.Attribute("value")!);
        Assert.Equal(2, sources.Elements("add").Count());
        Assert.Equal(["github", "nuget.org"], byKey.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("https://api.nuget.org/v3/index.json", byKey["nuget.org"]);
        Assert.Equal("https://nuget.pkg.github.com/nothisisnotdaniel/index.json", byKey["github"]);

        Dictionary<string, string[]> mapping = config.Root!.Element("packageSourceMapping")!.Elements("packageSource")
            .ToDictionary(
                source => (string)source.Attribute("key")!,
                source => source.Elements("package").Select(package => (string)package.Attribute("pattern")!).ToArray());
        Assert.Equal(["RoomKit"], mapping["github"]);
        Assert.Equal(["*"], mapping["nuget.org"]);
        Assert.Equal(2, mapping.Count);
        Assert.Null(config.Root!.Element("packageSourceCredentials"));
        Assert.DoesNotContain("<packageSourceCredentials", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Library/Application Support", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%HOME%", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ControlRoom", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Csproj_PinsRoomKitToExactly010()
    {
        XDocument project = XDocument.Parse(RepoFiles.Read("src/PRWatch/PRWatch.csproj"));

        XElement[] references = [.. project.Descendants("PackageReference")];
        XElement roomKit = Assert.Single(references);
        Assert.Equal("RoomKit", (string?)roomKit.Attribute("Include"));
        Assert.Equal("[0.1.0]", (string?)roomKit.Attribute("Version"));
        Assert.Equal("Microsoft.NET.Sdk.Web", (string?)project.Root!.Attribute("Sdk"));
    }

    [Fact]
    public void RoomKit_IsTheRestoredPackage010()
    {
        Assembly roomKit = typeof(RoomKitApp).Assembly;

        Assert.Equal(
            "0.1.0",
            roomKit.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

    [Fact]
    public void ProgramCs_IsTheOneRoomKitLine()
    {
        string[] lines = [.. RepoFiles.Read("src/PRWatch/Program.cs").Split('\n')
            .Where(line => line.Length > 0 && !line.StartsWith("using ", StringComparison.Ordinal))];

        Assert.Equal(
            ["return RoomKitApp.Run(args, PrWatchApp.Options, PrWatchApp.Map, addServices: PrWatchApp.AddServices);"],
            lines);
    }

    [Fact]
    public void Options_ArePRWatchs()
    {
        RoomKitOptions options = PrWatchApp.Options;

        Assert.Equal("PRWatch", options.AppName);
        Assert.Equal("PRWatch", options.ShortName);
        Assert.Equal("PRWatch", options.DataFolderName);
        Assert.Equal("PRWatch.slnx", options.SolutionFileName);
        Assert.Equal(8101, options.DefaultPort);
        Assert.Equal("nothisisnotdaniel/prwatch", options.ReleaseRepository);
        Assert.Null(options.CaptureNote);
        Assert.Equal(
            typeof(PrWatchApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            options.InformationalVersion);
        Assert.Equal("dev", options.InformationalVersion);
    }

    [Fact]
    public void Source_HasNoFactoryModuleOrJobFormat()
    {
        string src = RepoFiles.PathOf("src");
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(src, file)))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("FactoryModule", text, StringComparison.Ordinal);
            Assert.DoesNotContain("IJobFormat", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Tests_NeverWriteTheProcessEnvironment()
    {
        // Built by concatenation, so this file does not match itself.
        string needle = "Environment." + "SetEnvironmentVariable";
        string tests = RepoFiles.PathOf("tests/PRWatch.Tests");
        string[] files = [.. Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(tests, file))];
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            Assert.False(File.ReadAllText(file).Contains(needle, StringComparison.Ordinal),
                "writes the process environment: " + Path.GetRelativePath(tests, file));
        }
    }

    [Fact]
    public void Ci_RunsOnEveryPushAndPr_ReadOnly_OnMacos15()
    {
        string ci = RepoFiles.Read(".github/workflows/ci.yml");

        Assert.Contains("on:\n  push:\n  pull_request:\n", ci, StringComparison.Ordinal);
        Assert.Contains("\npermissions:\n  contents: read\n\njobs:\n", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("write", ci, StringComparison.Ordinal);
        Assert.Contains("runs-on: macos-15", ci, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_TestJob_ReadsContentsAndPackagesOnly()
    {
        string ci = RepoFiles.Read(".github/workflows/ci.yml");

        Assert.Contains(
            "\n  test:\n    name: Build and test\n    runs-on: macos-15\n" +
                "    # GitHub Packages needs a token even to read RoomKit; only the Restore step gets it.\n" +
                "    permissions:\n      contents: read\n      packages: read\n    timeout-minutes: 30\n",
            ci,
            StringComparison.Ordinal);
        Assert.Equal(2, ci.Split('\n').Count(line => line.TrimStart().StartsWith("permissions:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Ci_PinsEveryActionByFullSha()
    {
        string[] uses = [.. RepoFiles.Read(".github/workflows/ci.yml").Split('\n')
            .Where(line => line.TrimStart().StartsWith("uses:", StringComparison.Ordinal) ||
                line.TrimStart().StartsWith("- uses:", StringComparison.Ordinal))];

        Assert.NotEmpty(uses);
        foreach (string line in uses)
        {
            Assert.Matches(@"uses: [\w.-]+/[\w.-]+@[0-9a-f]{40}( #.*)?$", line);
        }
    }

    [Fact]
    public void Ci_RestoresThenChecksRoomKit_ThenBuildsAndTestsWithoutRestoring()
    {
        string ci = RepoFiles.Read(".github/workflows/ci.yml");

        AssertInOrder(
            ci,
            [
                "      - name: Restore\n        env:\n          " + Credential + "\n        run: dotnet restore\n",
                "      - name: Check the RoomKit package\n        env:\n          GH_TOKEN: ${{ github.token }}\n        run: |\n",
                "      - name: Build\n        run: dotnet build -warnaserror --no-restore\n",
                "      - name: Test\n        run: dotnet test --no-build\n",
            ]);
        Assert.Equal(1, Count(ci, Credential));
        Assert.Equal(1, Count(ci, "GH_TOKEN"));
        Assert.Equal(1, Count(ci, "secrets."));
        Assert.Equal(1, Count(ci, "github.token"));
        Assert.Equal(1, Count(ci, "dotnet restore"));
        Assert.Equal(3, Count(ci, "run: dotnet "));
    }

    [Fact]
    public void Workflows_GiveTheRegistryTokenOnlyToTheFourRestoreSteps()
    {
        string all = RepoFiles.Read(".github/workflows/ci.yml") + RepoFiles.Read(".github/workflows/release.yml");

        Assert.Equal(4, Count(all, Credential));
        Assert.Equal(4, Count(all, "NuGetPackageSourceCredentials_github: "));
        Assert.Equal(4, Count(all, "secrets."));
        Assert.Equal(4, Count(all, "      - name: Restore\n        env:\n          " + Credential + "\n        run: dotnet restore"));
        Assert.Equal(4, Count(all, "      packages: read\n"));
        Assert.DoesNotContain("packages: write", all, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData("README.md")]
    [InlineData("nuget.config")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/release.yml")]
    public void Files_NameNeitherControlRoomsDocsNorItsRepo(string file)
    {
        string text = RepoFiles.Read(file);

        Assert.DoesNotContain("ControlRoom/docs/roomkit.md", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothisisnotdaniel/controlroom", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/release.yml")]
    public void Workflows_HaveNoFolderFeedOrReleaseDownload(string file)
    {
        string text = RepoFiles.Read(file);

        Assert.DoesNotContain("gh release download", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RoomKit/feed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Fill the RoomKit feed", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData("README.md")]
    public void Docs_ExplainTheGitHubPackagesSetup(string file)
    {
        string text = RepoFiles.Read(file);

        Assert.Contains("https://github.com/nothisisnotdaniel/roomkit/blob/main/docs/roomkit.md", text, StringComparison.Ordinal);
        Assert.Contains("https://nuget.pkg.github.com/nothisisnotdaniel/index.json", text, StringComparison.Ordinal);
        Assert.Contains("`read:packages`", text, StringComparison.Ordinal);
        Assert.Contains("classic", text, StringComparison.Ordinal);
        Assert.Contains("NuGetPackageSourceCredentials_github=Username=<user>;Password=<token>", text, StringComparison.Ordinal);
        Assert.Contains("never committed", text, StringComparison.Ordinal);
        Assert.Contains("`scripts/restore.sh`", text, StringComparison.Ordinal);
        Assert.Contains("prwatch-github-packages", text, StringComparison.Ordinal);
        Assert.Contains("~/Library/Application Support/PRWatch/github-packages", text, StringComparison.Ordinal);
        Assert.Contains("never export", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("untrusted PR code", text, StringComparison.Ordinal);
        Assert.DoesNotContain("export NuGetPackageSourceCredentials_github='", text, StringComparison.Ordinal);
        Assert.DoesNotContain("export NuGetPackageSourceCredentials_github=Username", text, StringComparison.Ordinal);
        Assert.DoesNotContain("export 'NuGetPackageSourceCredentials_github", text, StringComparison.Ordinal);
        Assert.Contains("`GITHUB_TOKEN`", text, StringComparison.Ordinal);
        Assert.Contains("`Restore`", text, StringComparison.Ordinal);
        Assert.Contains("`roomkit.sha256` in the same commit", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_AddsTheKeychainItemBehindAPrompt()
    {
        string text = RepoFiles.Read("README.md");

        Assert.Contains("security add-generic-password -s prwatch-github-packages -a <user> -T '' -w", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"add-generic-password[^\n]*-a <user> -w"), text);
        Assert.All(
            text.Split('\n').Where(line => line.Contains("add-generic-password", StringComparison.Ordinal)),
            line => Assert.Contains("-T ''", line, StringComparison.Ordinal));
        Assert.Contains("\"Allow\", not \"Always Allow\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_TellsHowToReplaceAnOpenItem_AndWarnsAboutTheFile()
    {
        string text = RepoFiles.Read("README.md");

        Assert.Contains("security delete-generic-password -s prwatch-github-packages", text, StringComparison.Ordinal);
        string flat = Regex.Replace(text, @"\s+", " ");
        Assert.Contains(
            flat.Split(". "),
            sentence => sentence.Contains("0600", StringComparison.Ordinal) && sentence.Contains("any code", StringComparison.Ordinal));
    }

    [Fact]
    public void RoomKitPin_IsOneShasumLine_ForTheCsprojVersion()
    {
        string pin = RepoFiles.Read("roomkit.sha256");
        Match version = Regex.Match(
            RepoFiles.Read("src/PRWatch/PRWatch.csproj"),
            @"<PackageReference Include=""RoomKit"" Version=""\[([0-9][0-9.]*)\]""");

        Assert.True(version.Success);
        Assert.EndsWith("\n", pin, StringComparison.Ordinal);
        string[] lines = pin[..^1].Split('\n');
        string line = Assert.Single(lines);
        Match match = Regex.Match(line, @"^[0-9a-f]{64}  RoomKit\.(?<v>.+)\.nupkg$");
        Assert.True(match.Success, line);
        Assert.Equal(version.Groups[1].Value, match.Groups["v"].Value);
    }

    [Fact]
    public void Ci_CheckStep_HashesTheRestoredFile_ComparesThePin_ThenVerifiesTheAttestation()
    {
        string ci = RepoFiles.Read(".github/workflows/ci.yml");
        string[] parts =
        [
            "echo \"No pinned RoomKit version in src/PRWatch/PRWatch.csproj.\" >&2",
            "package=\"${NUGET_PACKAGES:-$HOME/.nuget/packages}/roomkit/$version/roomkit.$version.nupkg\"",
            "actual=$(shasum -a 256 \"$package\" | awk '{ print $1 }')",
            "' roomkit.sha256)",
            "echo \"RoomKit.$version.nupkg does not match roomkit.sha256.\" >&2",
            "gh attestation verify \"$package\" --repo nothisisnotdaniel/roomkit " +
                "--cert-identity https://github.com/nothisisnotdaniel/roomkit/.github/workflows/release.yml@refs/heads/main " +
                "--cert-oidc-issuer https://token.actions.githubusercontent.com " +
                "--source-ref refs/heads/main --deny-self-hosted-runners --format json --jq '",
            "|| [ \"$verified\" != \"true\" ]; then",
            "echo \"RoomKit.$version.nupkg has no valid roomkit release attestation.\" >&2",
            "run: dotnet build -warnaserror --no-restore",
        ];

        int last = -1;
        foreach (string part in parts)
        {
            int at = ci.IndexOf(part, StringComparison.Ordinal);
            Assert.True(at > last, part);
            Assert.Equal(at, ci.LastIndexOf(part, StringComparison.Ordinal));
            last = at;
        }

        Assert.Equal(1, Regex.Count(ci, @"gh attestation verify"));
        Assert.DoesNotContain("--signer-workflow", ci, StringComparison.Ordinal);
    }

    private const string Credential = "NuGetPackageSourceCredentials_github: Username=github;Password=${{ secrets.GITHUB_TOKEN }}";

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

    private static bool IsBuildOutput(string src, string file)
    {
        string[] parts = Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar);
        return parts.Contains("bin") || parts.Contains("obj");
    }
}
