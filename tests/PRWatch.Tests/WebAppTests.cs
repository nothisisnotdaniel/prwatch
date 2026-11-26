using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PRWatch.Tests;

/// <summary>The minimal web app: <c>index.html</c>, <c>room.js</c>, <c>app.js</c>, the manifest and RoomKit's shell
/// files, fetched in memory and checked as text (no browser).</summary>
public sealed partial class WebAppTests
{
    private static readonly string[] RoomKitShellFiles = ["/app.css", "/token.js", "/sw.js"];

    [Fact]
    public async Task Root_WithoutToken_ServesIndexWithCspAndTheShellLinks()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            RoomKitApp.ContentSecurityPolicy,
            string.Join(",", response.Headers.GetValues("Content-Security-Policy")));
        Assert.Contains("<meta name=\"color-scheme\" content=\"light dark\">", html, StringComparison.Ordinal);
        Assert.Contains("<link rel=\"stylesheet\" href=\"/app.css\">", html, StringComparison.Ordinal);
        Assert.Contains("<link rel=\"manifest\" href=\"/manifest.webmanifest\">", html, StringComparison.Ordinal);
        Assert.Equal(
            ["<script src=\"/room.js\" defer></script>", "<script src=\"/token.js\" defer></script>",
                "<script src=\"/app.js\" defer></script>"],
            ScriptTag().Matches(html).Select(match => match.Value));
    }

    [Fact]
    public async Task Index_HasNoInlineCodeOrStyle_AndNoOffOriginUrl()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        string html = await client.GetStringAsync("/");

        Assert.DoesNotMatch(InlineScript(), html);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(StyleAttribute(), html);
        Assert.DoesNotMatch(EventAttribute(), html);
        foreach (Match url in UrlAttribute().Matches(html))
        {
            string value = url.Groups["url"].Value;
            Assert.True(value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal), value);
        }

        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/app.css")]
    [InlineData("/token.js")]
    [InlineData("/sw.js")]
    [InlineData("/room.js")]
    [InlineData("/app.js")]
    [InlineData("/manifest.webmanifest")]
    public async Task ShellFile_Is200(string path)
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void RoomJs_DeclaresEveryValueRoomKitReads()
    {
        string room = RepoFiles.Read("src/PRWatch/wwwroot/room.js");

        Assert.Contains("const TokenKey = \"prwatch-token\";", room, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"const PushAppTexts = \{[^}]*\bhomeScreen: ""[^""]+"",[^}]*\bdenied: ""[^""]+"","), room);
        Assert.Matches(new Regex(@"const CacheVersion = ""v\d+"";"), room);
        Assert.Contains("const CacheName = \"prwatch-shell-\" + CacheVersion;", room, StringComparison.Ordinal);
        Assert.Contains("const Shell = [", room, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"const PushAppTitles = \{[^}]*\bupdated: ""[^""]+"","), room);
        Assert.Matches(new Regex(@"const PushFallbackTitle = ""[^""]+"";"), room);
        Assert.Matches(new Regex(@"const PushDefaultTag = ""[^""]+"";"), room);
        Assert.Matches(new Regex(@"const PushUpdatePrefix = ""[^""]+"";"), room);
    }

    [Fact]
    public async Task Shell_ListsEveryOwnWebFile_AndEveryPathIs200()
    {
        string[] shell = ReadShell();
        string webRoot = RepoFiles.PathOf("src/PRWatch/wwwroot");
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        foreach (string file in Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories))
        {
            string path = "/" + Path.GetRelativePath(webRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            Assert.Contains(path, shell);
        }

        foreach (string path in RoomKitShellFiles.Where(path => path != "/sw.js"))
        {
            Assert.Contains(path, shell);
        }

        foreach (string path in shell)
        {
            using HttpResponseMessage response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, path);
        }
    }

    [Fact]
    public async Task Manifest_NamesPRWatch_Standalone_AndItsIconsAre200()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using JsonDocument manifest = JsonDocument.Parse(await client.GetStringAsync("/manifest.webmanifest"));
        JsonElement root = manifest.RootElement;

        Assert.Equal("PRWatch", root.GetProperty("name").GetString());
        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        JsonElement[] icons = [.. root.GetProperty("icons").EnumerateArray()];
        Assert.NotEmpty(icons);
        foreach (JsonElement icon in icons)
        {
            string src = icon.GetProperty("src").GetString()!;
            using HttpResponseMessage response = await client.GetAsync(src);
            Assert.True(response.StatusCode == HttpStatusCode.OK, src);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public void AppJs_ShowsTheVersionAsText_AfterSignIn()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");

        Assert.Contains("running: \"PRWatch is running\"", app, StringComparison.Ordinal);
        Assert.Contains("const VersionRoute = \"/api/version\";", app, StringComparison.Ordinal);
        Assert.Contains("authFetch(VersionRoute", app, StringComparison.Ordinal);
        Assert.Contains(".textContent = text;", app, StringComparison.Ordinal);
        Assert.Contains("onTokenSaved(loadHome);", app, StringComparison.Ordinal);
        Assert.Contains("hasToken()", app, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("innerHTML")]
    [InlineData("outerHTML")]
    [InlineData("insertAdjacentHTML")]
    [InlineData("eval(")]
    [InlineData("new Function")]
    [InlineData("document.write")]
    [InlineData("XMLHttpRequest")]
    public void AppJs_HasNoUnsafeSink(string sink)
    {
        Assert.DoesNotContain(sink, RepoFiles.Read("src/PRWatch/wwwroot/app.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_FetchesOnlyApiPaths()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");

        // Only authFetch (which sends the token) and only with a route constant that starts with /api/.
        Assert.DoesNotMatch(new Regex(@"(?<!auth)fetch\("), app);
        MatchCollection calls = new Regex(@"authFetch\((?<arg>[^,)]+)").Matches(app);
        Assert.NotEmpty(calls);
        foreach (Match call in calls)
        {
            string arg = call.Groups["arg"].Value.Trim();
            Match constant = new Regex("const " + Regex.Escape(arg) + @" = ""(?<path>[^""]*)"";").Match(app);
            Assert.True(constant.Success, arg);
            Assert.StartsWith("/api/", constant.Groups["path"].Value, StringComparison.Ordinal);
        }
    }

    private static string[] ReadShell()
    {
        string room = RepoFiles.Read("src/PRWatch/wwwroot/room.js");
        Match list = ShellList().Match(room);
        Assert.True(list.Success);
        return [.. QuotedString().Matches(list.Groups["body"].Value).Select(match => match.Groups["value"].Value)];
    }

    [GeneratedRegex("<script[^>]*>[^<]*</script>")]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"<script(?![^>]*\bsrc=)[^>]*>|<script[^>]*>[^<\s]+</script>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttribute();

    [GeneratedRegex(@"\s(?:src|href|action)=""(?<url>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlAttribute();

    [GeneratedRegex(@"const Shell = \[(?<body>[^\]]*)\];")]
    private static partial Regex ShellList();

    [GeneratedRegex(@"""(?<value>[^""]*)""")]
    private static partial Regex QuotedString();
}
