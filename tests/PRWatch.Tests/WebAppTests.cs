using System.Net;
using System.Reflection;
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
        Assert.Contains("<link rel=\"stylesheet\" href=\"/prwatch.css\">", html, StringComparison.Ordinal);
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

    [Fact]
    public async Task Index_ProjectsHelp_NamesFactoryAsAChoice_AndNoSelectionAsEveryProject()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        string html = await client.GetStringAsync("/index.html");

        Match help = ProjectsHelp().Match(html);
        Assert.True(help.Success);
        string text = help.Groups["text"].Value;
        Assert.Contains("None selected: every project", text, StringComparison.Ordinal);
        Assert.Contains("factory", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/app.css")]
    [InlineData("/token.js")]
    [InlineData("/sw.js")]
    [InlineData("/room.js")]
    [InlineData("/app.js")]
    [InlineData("/prwatch.css")]
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
        Assert.Contains("const CacheVersion = \"v2\";", room, StringComparison.Ordinal);
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
    public void AppJs_LoadsThePageAfterSignIn_WithFixedTexts()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");

        Assert.Contains("onTokenSaved(loadHome);", app, StringComparison.Ordinal);
        Assert.Contains("hasToken()", app, StringComparison.Ordinal);
        Assert.Contains("showTokenView(false);", app, StringComparison.Ordinal);
        Assert.Contains("node.textContent = text;", app, StringComparison.Ordinal);
    }

    // --- job 426: the page ---------------------------------------------------------------------

    [Fact]
    public async Task Index_HasTheStatusBarSwitchListAndAControlForEverySettingButEnabled()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        string html = await client.GetStringAsync("/");

        Assert.Contains("id=\"status-bar\"", html, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"<input[^>]*\bid=""enabled""[^>]*\btype=""checkbox"""), html);
        Assert.Contains("id=\"prs\"", html, StringComparison.Ordinal);
        Match form = new Regex(@"<form[^>]*\bid=""settings-form""[^>]*>(?<body>.*?)</form>", RegexOptions.Singleline)
            .Match(html);
        Assert.True(form.Success);
        string[] names = [.. new Regex(@"<(?:input|select|textarea)[^>]*\bname=""(?<name>[^""]+)""")
            .Matches(form.Groups["body"].Value).Select(match => match.Groups["name"].Value)];
        Assert.Equal(SettingsRules.FieldNames.Where(name => name != "enabled").Order(StringComparer.Ordinal),
            names.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("innerHTML")]
    [InlineData("outerHTML")]
    [InlineData("insertAdjacentHTML")]
    [InlineData("document.write")]
    [InlineData("eval")]
    [InlineData("console.")]
    public void AppJs_NeverUsesAnHtmlSinkEvalOrTheConsole(string sink)
    {
        Assert.DoesNotContain(sink, RepoFiles.Read("src/PRWatch/wwwroot/app.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_SetsPrTextThroughTextContent_AndLinksOnlyACheckedGitHubPullUrl()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");

        Assert.Contains(
            @"const PullUrl = /^https:\/\/github\.com\/[^/]+\/[^/]+\/pull\/[0-9]+$/;", app, StringComparison.Ordinal);
        Assert.Contains("node.textContent = text;", app, StringComparison.Ordinal);
        // The only href is set inside the PullUrl check, with rel noopener noreferrer.
        MatchCollection hrefs = new Regex(@"\.href = (?<value>[^;]+);").Matches(app);
        Match href = Assert.Single(hrefs);
        Assert.Equal("pr.url", href.Groups["value"].Value);
        string before = app[..href.Index];
        int check = before.LastIndexOf("PullUrl.test(pr.url)", StringComparison.Ordinal);
        Assert.True(check > before.LastIndexOf("function ", StringComparison.Ordinal), "href outside the url check");
        Assert.Contains("link.rel = \"noopener noreferrer\";", app, StringComparison.Ordinal);
        Assert.Contains("pr.url.endsWith(\"/pull/\" + String(pr.number))", app, StringComparison.Ordinal);
        Assert.DoesNotContain("setAttribute(", app, StringComparison.Ordinal);
        Assert.Contains("capped(pr.title, MaxTitle)", app, StringComparison.Ordinal);
        Assert.Contains("capped(pr.author, MaxAuthor)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_CallsAuthFetchOnlyWithTheRouteConstants()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");
        Dictionary<string, string> routes = new(StringComparer.Ordinal)
        {
            ["StatusRoute"] = PrWatchApp.StatusRoute,
            ["PrsRoute"] = PrWatchApp.PrsRoute,
            ["SettingsRoute"] = PrWatchApp.SettingsRoute,
            ["ReviewRoute"] = PrWatchApp.ReviewRoute,
            ["MergeRoute"] = PrWatchApp.MergeRoute,
            ["CancelRoute"] = PrWatchApp.CancelRoute,
        };
        foreach (KeyValuePair<string, string> route in routes)
        {
            Assert.Contains("const " + route.Key + " = \"" + route.Value + "\";", app, StringComparison.Ordinal);
        }

        string[] args = [.. new Regex(@"authFetch\((?<arg>[^,)]*)").Matches(app)
            .Select(match => match.Groups["arg"].Value.Trim())];
        Assert.NotEmpty(args);
        Assert.All(args, arg => Assert.Contains(arg, routes.Keys));
        Assert.Equal(routes.Keys.Order(StringComparer.Ordinal), args.Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AppJs_TapsPostTheRenderedRowsHead_NameItsShortHead_ConfirmConsentAndMerge_AndReload()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");

        Assert.Contains(
            "const body = { project: pr.project, number: pr.number, headSha: pr.headSha };", app, StringComparison.Ordinal);
        Assert.Contains("if (action === \"consent\") {\n    body.consent = true;", app, StringComparison.Ordinal);
        Assert.Contains("body: JSON.stringify(body),", app, StringComparison.Ordinal);
        Assert.Contains("const ShortSha = 7;", app, StringComparison.Ordinal);
        Assert.Contains("return sha.slice(0, ShortSha);", app, StringComparison.Ordinal);
        Assert.Contains("label + \" · \" + head", app, StringComparison.Ordinal);
        Assert.Contains(
            "const confirm = action === \"consent\" ? Texts.confirmConsent : action === \"merge\" ? Texts.confirmMerge : \"\";",
            app, StringComparison.Ordinal);
        Assert.Contains("if (!armedFor(action, pr)) {", app, StringComparison.Ordinal);
        Assert.Contains("button.textContent = confirm + head;", app, StringComparison.Ordinal);
        Match tap = new Regex(@"async function tap\(action, pr\) \{(?<body>.*?)\n\}", RegexOptions.Singleline).Match(app);
        Assert.True(tap.Success);
        Assert.EndsWith("await refresh();", tap.Groups["body"].Value.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_ArmsOneConsentOrMergeButton_ForArmSeconds()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");
        string button = Body(app, "function actionButton(action, pr)");

        // AC-1: one constant, one armed state, and arming replaces the earlier one.
        Assert.Single(Regex.Matches(app, @"const ArmSeconds = 5;"));
        Assert.Single(Regex.Matches(app, @"let armed = null;"));
        Assert.Matches(new Regex(
            @"disarm\(\);\s*armed = \{ project: pr\.project, number: pr\.number, action: action, headSha: pr\.headSha,"
            + @"\s*until: Date\.now\(\) \+ ArmSeconds \* 1000 \};"), button);

        // AC-2: a timer of ArmSeconds disarms, and the disarm draws the button unarmed.
        Assert.Contains("armTimer = setTimeout(disarm, ArmSeconds * 1000);", button, StringComparison.Ordinal);
        string disarm = Body(app, "function disarm()");
        Assert.Contains("clearTimeout(armTimer);", disarm, StringComparison.Ordinal);
        Assert.Contains("armedButton.isConnected", disarm, StringComparison.Ordinal);
        Assert.Contains("armedButton.classList.remove(\"armed\");", disarm, StringComparison.Ordinal);
        Assert.Contains("delete armedButton.dataset.armed;", disarm, StringComparison.Ordinal);
        Assert.Contains("armedButton.textContent = label + \" · \" + head;", disarm, StringComparison.Ordinal);
        Assert.Contains("const head = shortSha(armed.headSha);", disarm, StringComparison.Ordinal);
        Assert.EndsWith("armed = null;\n  armedButton = null;", disarm.TrimEnd(), StringComparison.Ordinal);

        // AC-3: the second tap sends only on a match of all four fields inside the window, and clears the state first.
        Assert.Equal(
            "return armed !== null && armed.project === pr.project && armed.number === pr.number && armed.action === action;",
            Body(app, "function armedForPr(action, pr)").Trim());
        Assert.Equal(
            "return armedForPr(action, pr) && armed.headSha === pr.headSha && Date.now() < armed.until;",
            Body(app, "function armedFor(action, pr)").Trim());
        Assert.Contains(
            "      return;\n    }\n    armed = null;\n    armedButton = null;\n    if (armTimer !== null) {\n      clearTimeout(armTimer);\n      armTimer = null;\n    }\n"
            + "    button.disabled = true;\n    tap(action, pr);\n  });",
            button, StringComparison.Ordinal);

        // AC-4: a redraw inside the window draws the matching button armed; another head clears the state.
        Assert.Contains(
            "if (confirm && armedFor(action, pr)) {\n    // A refresh inside the armed window draws the button armed again.\n"
            + "    button.dataset.armed = \"yes\";\n    button.classList.add(\"armed\");\n"
            + "    button.textContent = confirm + head;\n    armedButton = button;\n"
            + "  } else if (confirm && armedForPr(action, pr)) {\n", button, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"\} else if \(confirm && armedForPr\(action, pr\)\) \{\n\s*//[^\n]*\n\s*disarm\(\);\n  \}"),
            button);

        // AC-6: Cancel sends on one tap and never arms.
        Assert.Contains(
            "if (confirm === \"\") {\n      button.disabled = true;\n      tap(action, pr);\n      return;\n    }", button,
            StringComparison.Ordinal);
        Assert.True(button.IndexOf("if (confirm === \"\")", StringComparison.Ordinal)
            < button.IndexOf("armed = {", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(app, @"armed = \{"));
    }

    [Fact]
    public void AppJs_HasAFixedTextForEveryRequestAndSettingsError_AndShowsNoOtherText()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");

        AssertMapHas(app, "RequestErrors", ["invalid_request", "unknown_pr", "stale_request", "write_failed"]);
        AssertMapHas(app, "SettingsErrors", ["invalid_settings", "save_failed"]);
        AssertMapHas(app, "SettingsFieldErrors", [.. SettingsRules.FieldNames, SettingsRules.UnknownKey]);
        Assert.Matches(new Regex(@"const NetworkError = ""[^""]+"";"), app);
        Assert.Matches(new Regex(@"const ErrorFallback = ""[^""]+"";"), app);
        Assert.Matches(new Regex(@"const FactoryRootFirst = ""Fix the factory root first[^""]*"";"), app);
        Assert.Contains("textFor(RequestErrors, error && error.error, ErrorFallback)", app, StringComparison.Ordinal);
        Assert.Contains("textFor(SettingsErrors, code, ErrorFallback)", app, StringComparison.Ordinal);
        Assert.Contains(
            "} else if (name === \"factoryRoot\" && !Object.prototype.hasOwnProperty.call(changed, \"factoryRoot\")) {\n"
            + "      showMessage(\"error-factoryRoot\", FactoryRootFirst);",
            app, StringComparison.Ordinal);
        // No response text is ever shown: no .text() read, and every error is looked up in a map.
        Assert.DoesNotContain(".text()", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".message", app, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_ShowsAFixedTextForAnUnusedRequest_ThroughTextContent_InPlaceOfTheWaitingLine()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");

        Assert.Contains(
            "const RequestUnused = \"The last run did not use this request. Tap again to send it once more.\";",
            app, StringComparison.Ordinal);
        Assert.Contains(
            "  if (pr.request && pr.requestUnused === true) {\n"
            + "    row.append(element(\"p\", \"pr-request\", RequestUnused));\n"
            + "  } else if (pr.request) {\n"
            + "    row.append(element(\"p\", \"pr-request\", textFor(Requests, pr.request, Fallback)));\n"
            + "  }",
            app, StringComparison.Ordinal);
        // element() sets its text through textContent only.
        Assert.Contains("node.textContent = text;", app, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_HasAFixedTextForEveryStatusCode()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");
        string[] errors = [.. typeof(StatusError).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)];
        Assert.Contains(StatusError.SettingsUnreadable, errors);

        AssertMapHas(app, "StatusErrors", errors);
        AssertMapHas(app, "BrakeReasons", TokenBrake.Reasons);
        AssertMapHas(app, "LoginStates", LoginStatus.LoginStates);
        AssertMapHas(app, "SkipReasons", SkipStatus.Reasons);
        AssertMapHas(app, "RunResults", RunStatus.Results);
        AssertMapHas(app, "Stages", PrRules.Stages);
        AssertMapHas(app, "TestResults", PrRules.TestResults);
        AssertMapHas(app, "Requests", PrRules.Requests);
        Assert.Matches(new Regex(@"settings_unreadable: ""[^""]*defaults are in use[^""]*Save the settings[^""]*"""), app);
        Assert.Matches(new Regex(@"const Fallback = ""[^""]+"";"), app);
        Assert.Contains("brake.holding === true", app, StringComparison.Ordinal);
        Assert.Contains("formatTime(brake.until)", app, StringComparison.Ordinal);
        Assert.Contains("login.expiresSoon === true", app, StringComparison.Ordinal);
        Assert.Contains("formatTime(status && status.lastPoll)", app, StringComparison.Ordinal);
        Assert.Contains("textFor(StatusErrors, error.code, ErrorFallback)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_SwitchPostsOnlyEnabled_AndTheFormOnlyChangedFields()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");

        Assert.Contains("response = await postSettings({ enabled: enabled });", app, StringComparison.Ordinal);
        Assert.Contains("document.getElementById(\"enabled\").addEventListener(\"change\", toggleEnabled);", app,
            StringComparison.Ordinal);
        Assert.Contains("response = await postSettings(changes);", app, StringComparison.Ordinal);
        Assert.Contains(
            "if (JSON.stringify(values[name]) !== JSON.stringify(loadedSettings[name] === undefined ? null : loadedSettings[name])) {\n"
            + "      changes[name] = values[name];",
            app, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_RefreshesEvery30SecondsOnlyWhileVisible()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");

        Assert.Contains("const RefreshSeconds = 30;", app, StringComparison.Ordinal);
        Assert.Contains("refreshTimer = setInterval(refresh, RefreshSeconds * 1000);", app, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener(\"visibilitychange\", onVisibilityChange);", app,
            StringComparison.Ordinal);
        Match handler = new Regex(@"function onVisibilityChange\(\) \{(?<body>.*?)\n\}", RegexOptions.Singleline)
            .Match(app);
        Assert.True(handler.Success);
        Assert.Equal(
            "if (document.visibilityState === \"visible\") {\n    refresh();\n    startRefresh();\n  } else {\n"
            + "    stopRefresh();\n    disarm();\n  }",
            handler.Groups["body"].Value.Trim());
        Assert.Contains("clearInterval(refreshTimer);", app, StringComparison.Ordinal);
    }

    [Fact]
    public void PrWatchCss_IsMobileFirst_With44pxButtons_AndADarkBlock()
    {
        string css = RepoFiles.Read("src/PRWatch/wwwroot/prwatch.css");

        foreach (Match query in new Regex(@"min-width:\s*(?<px>\d+)px\s*\)").Matches(css))
        {
            Assert.True(int.Parse(query.Groups["px"].Value, System.Globalization.CultureInfo.InvariantCulture) <= 600,
                query.Value);
        }

        Assert.Contains("@media (prefers-color-scheme: dark) {", css, StringComparison.Ordinal);
        Match buttons = new Regex(@"\.action,\s*\.settings-form \.primary \{(?<body>[^}]*)\}").Match(css);
        Assert.True(buttons.Success);
        Assert.Contains("min-height: 44px;", buttons.Groups["body"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("@import", css, StringComparison.Ordinal);
        Assert.DoesNotContain("url(", css, StringComparison.Ordinal);
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

    private static void AssertMapHas(string app, string name, IEnumerable<string> codes)
    {
        Match map = new Regex("const " + Regex.Escape(name) + @" = \{\n(?<body>.*?)\n\};", RegexOptions.Singleline)
            .Match(app);
        Assert.True(map.Success, name);
        foreach (string code in codes)
        {
            Assert.Matches(new Regex(@"(?m)^  " + Regex.Escape(code) + @": ""[^""]+"","), map.Groups["body"].Value);
        }
    }

    private static string[] ReadShell()
    {
        string room = RepoFiles.Read("src/PRWatch/wwwroot/room.js");
        Match list = ShellList().Match(room);
        Assert.True(list.Success);
        return [.. QuotedString().Matches(list.Groups["body"].Value).Select(match => match.Groups["value"].Value)];
    }

    private static string Body(string source, string signature)
    {
        Match match = new Regex(Regex.Escape(signature) + @" \{(?<body>.*?)\n\}", RegexOptions.Singleline).Match(source);
        Assert.True(match.Success, signature);
        return match.Groups["body"].Value;
    }

    [Fact]
    public void SentTextShowsAfterAcceptedTap()
    {
        string app = RepoFiles.Read("src/PRWatch/wwwroot/app.js");
        string css = RepoFiles.Read("src/PRWatch/wwwroot/prwatch.css");
        Assert.Contains("sent: \"Sent\",", app, StringComparison.Ordinal);
        Assert.Contains("element(\"p\", \"pr-sent\", Texts.sent)", app, StringComparison.Ordinal);
        int tapStart = app.IndexOf("async function tap(", StringComparison.Ordinal);
        int okCheck = app.IndexOf("if (!response.ok)", tapStart, StringComparison.Ordinal);
        int assign = app.IndexOf("sent = { project", tapStart, StringComparison.Ordinal);
        int refresh = app.IndexOf("await refresh();", tapStart, StringComparison.Ordinal);
        Assert.True(okCheck > 0 && okCheck < assign && assign < refresh);
        Assert.Contains("sent.headSha === pr.headSha", app, StringComparison.Ordinal);
        Assert.Contains("else if (isSent(pr))", app, StringComparison.Ordinal);
        Assert.Contains("if (!match || match.request)", app, StringComparison.Ordinal);
        Assert.Contains("clearSentIfDone(prs);", app, StringComparison.Ordinal);
        string tap = app[tapStart..refresh];
        Assert.DoesNotContain("action ===", tap[..tap.IndexOf("sent = {", StringComparison.Ordinal)][tap.IndexOf("const body", StringComparison.Ordinal)..].Replace("action === \"consent\"", ""), StringComparison.Ordinal);
        Assert.Contains(".pr-sent", css, StringComparison.Ordinal);
        Assert.Contains("prefers-color-scheme: dark", css, StringComparison.Ordinal);
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

    [GeneratedRegex(@"<select id=""field-projects""[^>]*></select>\s*<p class=""field-help"">(?<text>[^<]*)</p>")]
    private static partial Regex ProjectsHelp();
}
