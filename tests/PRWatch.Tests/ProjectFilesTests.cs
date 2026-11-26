using System.Reflection;
using System.Xml.Linq;

namespace PRWatch.Tests;

/// <summary>The repo's build files, <c>Program.cs</c>, the options and the CI workflow, read as text.</summary>
public sealed class ProjectFilesTests
{
    [Fact]
    public void NugetConfig_MapsRoomKitOnlyToTheFolderFeed()
    {
        XDocument config = XDocument.Parse(RepoFiles.Read("nuget.config"));
        XElement sources = config.Root!.Element("packageSources")!;

        Assert.Equal("clear", sources.Elements().First().Name.LocalName);
        Dictionary<string, string> byKey = sources.Elements("add")
            .ToDictionary(add => (string)add.Attribute("key")!, add => (string)add.Attribute("value")!);
        Assert.Equal(["nuget.org", "roomkit"], byKey.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("https://api.nuget.org/v3/index.json", byKey["nuget.org"]);
        Assert.Equal("%HOME%/Library/Application Support/RoomKit/feed", byKey["roomkit"]);

        Dictionary<string, string[]> mapping = config.Root!.Element("packageSourceMapping")!.Elements("packageSource")
            .ToDictionary(
                source => (string)source.Attribute("key")!,
                source => source.Elements("package").Select(package => (string)package.Attribute("pattern")!).ToArray());
        Assert.Equal(["RoomKit"], mapping["roomkit"]);
        Assert.Equal(["*"], mapping["nuget.org"]);
        Assert.Equal(2, mapping.Count);
    }

    [Fact]
    public void Csproj_PinsRoomKitToExactly030()
    {
        XDocument project = XDocument.Parse(RepoFiles.Read("src/PRWatch/PRWatch.csproj"));

        XElement[] references = [.. project.Descendants("PackageReference")];
        XElement roomKit = Assert.Single(references);
        Assert.Equal("RoomKit", (string?)roomKit.Attribute("Include"));
        Assert.Equal("[0.3.0]", (string?)roomKit.Attribute("Version"));
        Assert.Equal("Microsoft.NET.Sdk.Web", (string?)project.Root!.Attribute("Sdk"));
    }

    [Fact]
    public void RoomKit_IsTheRestoredPackage030()
    {
        Assembly roomKit = typeof(RoomKitApp).Assembly;

        Assert.Equal(
            "0.3.0",
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
    public void Ci_RunsOnEveryPushAndPr_ReadOnly_OnMacos15()
    {
        string ci = RepoFiles.Read(".github/workflows/ci.yml");

        Assert.Contains("on:\n  push:\n  pull_request:\n", ci, StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read\n", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("write", ci, StringComparison.Ordinal);
        Assert.Contains("runs-on: macos-15", ci, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", ci, StringComparison.Ordinal);
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
    public void Ci_DownloadsAndVerifiesRoomKit_ThenBuildsAndTests()
    {
        string ci = RepoFiles.Read(".github/workflows/ci.yml");

        Assert.Contains("GH_TOKEN: ${{ github.token }}", ci, StringComparison.Ordinal);
        Assert.Contains("src/PRWatch/PRWatch.csproj", ci, StringComparison.Ordinal);
        Assert.Contains(
            "gh release download \"v$version\" --repo nothisisnotdaniel/controlroom", ci, StringComparison.Ordinal);
        Assert.Contains("--pattern \"RoomKit.$version.nupkg\" --pattern SHA256SUMS", ci, StringComparison.Ordinal);
        Assert.Contains("shasum -a 256", ci, StringComparison.Ordinal);
        Assert.Contains("[ \"$expected\" != \"$actual\" ]", ci, StringComparison.Ordinal);
        Assert.Contains("$HOME/Library/Application Support/RoomKit/feed", ci, StringComparison.Ordinal);
        int download = ci.IndexOf("gh release download", StringComparison.Ordinal);
        int check = ci.IndexOf("shasum -a 256", StringComparison.Ordinal);
        int build = ci.IndexOf("run: dotnet build -warnaserror", StringComparison.Ordinal);
        int test = ci.IndexOf("run: dotnet test", StringComparison.Ordinal);
        Assert.True(download < check && check < build && build < test);
    }

    private static bool IsBuildOutput(string src, string file)
    {
        string[] parts = Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar);
        return parts.Contains("bin") || parts.Contains("obj");
    }
}
