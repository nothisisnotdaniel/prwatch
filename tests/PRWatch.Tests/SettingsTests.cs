using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace PRWatch.Tests;

/// <summary><c>GET</c>/<c>POST /api/settings</c> and <c>prwatch-settings.json</c>: defaults, merge and save,
/// validation, the token gate, unreadable files, symlinks and RoomKit's own <c>settings.json</c>. Each test has its
/// own host, temp folder and temp factory root.</summary>
public sealed class SettingsTests
{
    private const string Route = "/api/settings";

    // RoomKit's update route: GET shows the stored autoUpdate (its settings route takes only POST, never used here).
    private const string UpdateRoute = "/api/update";

    [Fact]
    public async Task Get_WithoutFile_ReturnsTheDefaults_AndWritesNothing()
    {
        using PrWatchFactory factory = NewHost(out string root, out _);
        using HttpClient client = await Start(factory);

        using HttpResponseMessage response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode expected = Defaults(root);
        AssertJson(expected, await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(SettingsFile(factory)));
    }

    [Fact]
    public async Task Get_WithoutFactoryRoot_HasNullRoot_AndEmptyDerivedValues()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = await Start(factory);

        string body = await client.GetStringAsync(Route);

        JsonObject expected = Defaults(null);
        AssertJson(expected, body);
        Assert.False(File.Exists(SettingsFile(factory)));
    }

    [Fact]
    public async Task Get_WithAnInvalidConfiguredRoot_HasEmptyDerivedValues()
    {
        using PrWatchFactory factory = new();
        string empty = Directory.CreateDirectory(Path.Combine(factory.TempFolder, "empty")).FullName;
        factory.FactoryRoot = empty;
        using HttpClient client = await Start(factory);

        using JsonDocument body = JsonDocument.Parse(await client.GetStringAsync(Route));

        Assert.Equal(empty, body.RootElement.GetProperty("factoryRoot").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("effectiveMode").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("effectiveModel").ValueKind);
        Assert.Equal(0, body.RootElement.GetProperty("knownProjects").GetArrayLength());
        Assert.Equal(0, body.RootElement.GetProperty("knownModes").GetArrayLength());
    }

    [Fact]
    public async Task Post_MergesSavesAndKeeps_AcrossAFreshHost()
    {
        using PrWatchFactory factory = NewHost(out string root, out _);
        JsonObject expected = Defaults(root);
        expected["pollMinutes"] = 7;
        expected["projects"] = new JsonArray("Alpha");
        using (HttpClient client = await Start(factory))
        {
            using HttpResponseMessage posted = await Post(client, "{\"pollMinutes\":7,\"projects\":[\"Alpha\"]}");
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
            AssertJson(expected, await posted.Content.ReadAsStringAsync());
            AssertJson(expected, await client.GetStringAsync(Route));
        }

        Assert.True(File.Exists(SettingsFile(factory)));
        using PrWatchFactory second = new(factory.TempFolder, ownsFolder: false) { FactoryRoot = root };
        using HttpClient again = await Start(second);
        AssertJson(expected, await again.GetStringAsync(Route));
    }

    [Fact]
    public async Task Post_EveryField_IsSaved_AndModeAndModelFollowTheMode()
    {
        using PrWatchFactory factory = NewHost(out string root, out string claude);
        using HttpClient client = await Start(factory);
        string body = Fill("""
            {"enabled":true,"factoryRoot":"{root}","projects":["Beta","factory"],"pollMinutes":60,
             "quietMinutes":0,"autoReviewTrusted":false,"autoMergeTrusted":true,"reviewerMode":"cheap",
             "maxBudgetUsd":0.1,"brakeFiveHourPercent":1,"brakeSevenDayPercent":100,"brakeFiveHourUsd":500,
             "brakeSevenDayUsd":0.5,"brakeRunsPerHour":10,"claudePath":"{claude}"}
            """, factory.TempFolder, root, claude);

        using HttpResponseMessage response = await Post(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonObject expected = (JsonObject)JsonNode.Parse(body)!;
        expected["effectiveMode"] = "cheap";
        expected["effectiveModel"] = "haiku";
        expected["knownProjects"] = new JsonArray("Alpha", "Beta", "factory");
        expected["knownModes"] = new JsonArray("cheap", "quality");
        AssertJson(expected, await response.Content.ReadAsStringAsync());
        AssertJson(expected, await client.GetStringAsync(Route));
    }

    [Fact]
    public async Task Post_FactoryAlone_IsSaved_AndFactoryStaysAKnownProject()
    {
        using PrWatchFactory factory = NewHost(out _, out _);
        using HttpClient client = await Start(factory);

        using HttpResponseMessage response = await Post(client, "{\"projects\":[\"factory\"]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument saved = JsonDocument.Parse(await client.GetStringAsync(Route));
        Assert.Equal(["factory"],
            saved.RootElement.GetProperty("projects").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("factory",
            saved.RootElement.GetProperty("knownProjects").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Post_NullsResetToAllProjectsAndTheDefaultMode()
    {
        using PrWatchFactory factory = NewHost(out string root, out _);
        using HttpClient client = await Start(factory);
        using HttpResponseMessage first = await Post(client, "{\"projects\":[\"Alpha\"],\"reviewerMode\":\"cheap\"}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpResponseMessage second = await Post(client, "{\"projects\":null,\"reviewerMode\":null}");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        AssertJson(Defaults(root), await second.Content.ReadAsStringAsync());
    }

    public static TheoryData<string, string[]> InvalidBodies => new()
    {
        { "{\"pollMinutes\":0}", ["pollMinutes"] },
        { "{\"pollMinutes\":61}", ["pollMinutes"] },
        { "{\"pollMinutes\":7.5}", ["pollMinutes"] },
        { "{\"pollMinutes\":\"x\"}", ["pollMinutes"] },
        { "{\"pollMinutes\":null}", ["pollMinutes"] },
        { "{\"quietMinutes\":-1}", ["quietMinutes"] },
        { "{\"quietMinutes\":121}", ["quietMinutes"] },
        { "{\"maxBudgetUsd\":0.09}", ["maxBudgetUsd"] },
        { "{\"maxBudgetUsd\":20.01}", ["maxBudgetUsd"] },
        { "{\"maxBudgetUsd\":\"2\"}", ["maxBudgetUsd"] },
        { "{\"brakeFiveHourPercent\":0}", ["brakeFiveHourPercent"] },
        { "{\"brakeFiveHourPercent\":101}", ["brakeFiveHourPercent"] },
        { "{\"brakeSevenDayPercent\":0}", ["brakeSevenDayPercent"] },
        { "{\"brakeSevenDayPercent\":101}", ["brakeSevenDayPercent"] },
        { "{\"brakeFiveHourUsd\":0.4}", ["brakeFiveHourUsd"] },
        { "{\"brakeFiveHourUsd\":500.5}", ["brakeFiveHourUsd"] },
        { "{\"brakeSevenDayUsd\":0.4}", ["brakeSevenDayUsd"] },
        { "{\"brakeSevenDayUsd\":501}", ["brakeSevenDayUsd"] },
        { "{\"brakeRunsPerHour\":0}", ["brakeRunsPerHour"] },
        { "{\"brakeRunsPerHour\":11}", ["brakeRunsPerHour"] },
        { "{\"enabled\":\"yes\"}", ["enabled"] },
        { "{\"autoReviewTrusted\":1}", ["autoReviewTrusted"] },
        { "{\"autoMergeTrusted\":null}", ["autoMergeTrusted"] },
        { "{\"secretKey123\":1}", ["_unknown"] },
        { "{\"pollMinutes\":7,\"pollMinutes\":8}", ["pollMinutes"] },
        { "{\"factoryRoot\":\"relative/root\"}", ["factoryRoot"] },
        { "{\"factoryRoot\":\"{temp}/root/../root\"}", ["factoryRoot"] },
        { "{\"factoryRoot\":\"{temp}/link\"}", ["factoryRoot"] },
        { "{\"factoryRoot\":\"{temp}/empty\"}", ["factoryRoot"] },
        { "{\"factoryRoot\":\"{temp}/missing\"}", ["factoryRoot"] },
        { "{\"factoryRoot\":\"\"}", ["factoryRoot"] },
        { "{\"factoryRoot\":5}", ["factoryRoot"] },
        { "{\"projects\":[\"Gamma\"]}", ["projects"] },
        { "{\"projects\":[\"alpha\"]}", ["projects"] },
        { "{\"projects\":[\"NotAProject\"]}", ["projects"] },
        { "{\"projects\":[]}", ["projects"] },
        { "{\"projects\":[\"Alpha\",\"Alpha\"]}", ["projects"] },
        { "{\"projects\":\"Alpha\"}", ["projects"] },
        { "{\"projects\":[1]}", ["projects"] },
        { "{\"reviewerMode\":\"turbo\"}", ["reviewerMode"] },
        { "{\"reviewerMode\":[\"cheap\"]}", ["reviewerMode"] },
        { "{\"claudePath\":\"claude\"}", ["claudePath"] },
        { "{\"claudePath\":\"{temp}/plain.txt\"}", ["claudePath"] },
        { "{\"claudePath\":\"{temp}/bin\"}", ["claudePath"] },
        { "{\"claudePath\":\"{temp}/bin/../bin/claude\"}", ["claudePath"] },
        { "{\"claudePath\":\"{temp}/nothing\"}", ["claudePath"] },
        { "{\"enabled\":true}", ["enabled"] },
        { "{\"enabled\":true,\"claudePath\":\"{claude}\",\"factoryRoot\":null}", ["enabled"] },
        { "{\"quietMinutes\":999,\"brakeRunsPerHour\":0,\"pollMinutes\":0}",
            ["brakeRunsPerHour", "pollMinutes", "quietMinutes"] },
        { "{\"pollMinutes\":0,\"evil\":1}", ["_unknown", "pollMinutes"] },
        { "[]", [] },
        { "\"text\"", [] },
        { "42", [] },
        { "null", [] },
        { "{not json", [] },
        { "", [] },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Post_Invalid_Is400WithTheFieldNames_AndLeavesTheFile(string template, string[] fields)
    {
        using PrWatchFactory factory = NewHost(out string root, out string claude);
        Directory.CreateDirectory(Path.Combine(factory.TempFolder, "empty"));
        File.CreateSymbolicLink(Path.Combine(factory.TempFolder, "link"), root);
        File.WriteAllText(Path.Combine(factory.TempFolder, "plain.txt"), "not a program\n");
        using HttpClient client = await Start(factory);
        string body = Fill(template, factory.TempFolder, root, claude);
        string expected = "{\"error\":\"invalid_settings\",\"fields\":["
            + string.Join(",", fields.Select(field => "\"" + field + "\"")) + "]}";

        using HttpResponseMessage absent = await Post(client, body);
        Assert.Equal(HttpStatusCode.BadRequest, absent.StatusCode);
        Assert.Equal(expected, await absent.Content.ReadAsStringAsync());
        Assert.False(File.Exists(SettingsFile(factory)));

        using HttpResponseMessage valid = await Post(client, "{\"pollMinutes\":9}");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        byte[] before = File.ReadAllBytes(SettingsFile(factory));

        using HttpResponseMessage present = await Post(client, body);
        Assert.Equal(HttpStatusCode.BadRequest, present.StatusCode);
        Assert.Equal(expected, await present.Content.ReadAsStringAsync());
        Assert.Equal(before, File.ReadAllBytes(SettingsFile(factory)));
        using JsonDocument now = JsonDocument.Parse(await client.GetStringAsync(Route));
        Assert.Equal(9, now.RootElement.GetProperty("pollMinutes").GetInt32());
    }

    [Fact]
    public async Task Post_LargerThan16KB_Is400WithNoFields()
    {
        using PrWatchFactory factory = NewHost(out _, out _);
        using HttpClient client = await Start(factory);
        string body = "{\"pollMinutes\":7" + new string(' ', 16 * 1024) + "}";

        using HttpResponseMessage response = await Post(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_settings\",\"fields\":[]}", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(SettingsFile(factory)));
    }

    [Fact]
    public async Task Post_Exactly16KB_IsRead()
    {
        using PrWatchFactory factory = NewHost(out _, out _);
        using HttpClient client = await Start(factory);
        string start = "{\"pollMinutes\":7";
        string body = start + new string(' ', (16 * 1024) - start.Length - 1) + "}";

        using HttpResponseMessage response = await Post(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Routes_WithoutToken_Are401_AndPutIs405()
    {
        using PrWatchFactory factory = NewHost(out _, out _);
        using HttpClient client = await Start(factory);
        using HttpClient anonymous = factory.CreateClient();

        using HttpResponseMessage get = await anonymous.GetAsync(Route);
        using HttpResponseMessage post = await Post(anonymous, "{\"pollMinutes\":7}");
        using HttpResponseMessage status = await anonymous.GetAsync("/api/status");
        using HttpResponseMessage put = await client.PutAsync(
            Route, new StringContent("{\"pollMinutes\":7}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        Assert.Equal(RoomKitApp.UnauthorizedBody, await post.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.False(File.Exists(SettingsFile(factory)));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"pollMinutes\":\"x\",\"enabled\":true}")]
    [InlineData("{\"pollMinutes\":999,\"enabled\":true}")]
    [InlineData("{\"enabled\":true,\"surprise\":1}")]
    public async Task UnreadableFile_GivesDefaults_ReportsIt_AndIsKeptUntilAValidPost(string content)
    {
        using PrWatchFactory factory = NewHost(out string root, out _);
        string file = Path.Combine(factory.CreateDataFolder(), "prwatch-settings.json");
        File.WriteAllText(file, content);
        byte[] before = File.ReadAllBytes(file);
        using HttpClient client = await Start(factory);

        AssertJson(Defaults(root), await client.GetStringAsync(Route));
        using JsonDocument status = JsonDocument.Parse(await client.GetStringAsync("/api/status"));
        JsonElement error = status.RootElement.GetProperty("lastError");
        Assert.Equal("settings_unreadable", error.GetProperty("code").GetString());
        Assert.Equal("2026-11-26T10:00:00.000Z", error.GetProperty("at").GetString());
        Assert.Equal(before, File.ReadAllBytes(file));

        using HttpResponseMessage posted = await Post(client, "{\"pollMinutes\":8}");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllBytes(file));
        Assert.Equal(8, saved.RootElement.GetProperty("pollMinutes").GetInt32());
        Assert.False(saved.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task ValidFile_IsLoadedAtStart_WithoutAnError()
    {
        using PrWatchFactory factory = NewHost(out _, out _);
        string file = Path.Combine(factory.CreateDataFolder(), "prwatch-settings.json");
        File.WriteAllText(file, "{\"quietMinutes\":3}");
        using HttpClient client = await Start(factory);

        using JsonDocument settings = JsonDocument.Parse(await client.GetStringAsync(Route));
        using JsonDocument status = JsonDocument.Parse(await client.GetStringAsync("/api/status"));

        Assert.Equal(3, settings.RootElement.GetProperty("quietMinutes").GetInt32());
        Assert.Equal(5, settings.RootElement.GetProperty("pollMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("lastError").ValueKind);
    }

    [Fact]
    public async Task SymlinkedSettingsFile_IsNotWrittenThrough_AndPostIs500()
    {
        using PrWatchFactory factory = NewHost(out string root, out _);
        string outside = Path.Combine(factory.TempFolder, "outside.json");
        File.WriteAllText(outside, "{\"pollMinutes\":6}");
        byte[] before = File.ReadAllBytes(outside);
        string file = Path.Combine(factory.CreateDataFolder(), "prwatch-settings.json");
        File.CreateSymbolicLink(file, outside);
        using HttpClient client = await Start(factory);

        AssertJson(Defaults(root), await client.GetStringAsync(Route));
        using HttpResponseMessage posted = await Post(client, "{\"pollMinutes\":8}");

        Assert.Equal(HttpStatusCode.InternalServerError, posted.StatusCode);
        Assert.Equal("{\"error\":\"save_failed\"}", await posted.Content.ReadAsStringAsync());
        Assert.Equal(before, File.ReadAllBytes(outside));
        Assert.NotNull(new FileInfo(file).LinkTarget);
        using JsonDocument now = JsonDocument.Parse(await client.GetStringAsync(Route));
        Assert.Equal(5, now.RootElement.GetProperty("pollMinutes").GetInt32());
    }

    [Fact]
    public async Task RoomKitSettings_TokenAndFactoryRoot_StayUntouched()
    {
        using PrWatchFactory factory = NewHost(out string root, out string claude);
        string roomKit = Path.Combine(factory.CreateDataFolder(), "settings.json");
        File.WriteAllText(roomKit, "{\"autoUpdate\":true}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(roomKit, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        byte[] roomKitBefore = File.ReadAllBytes(roomKit);
        string rootBefore = TestFactoryRoot.Hash(root);
        using HttpClient client = await Start(factory);
        string token = factory.ReadToken();

        List<string> bodies = [await client.GetStringAsync(Route)];
        using (HttpResponseMessage posted = await Post(
            client, Fill("{\"enabled\":true,\"claudePath\":\"{claude}\"}", factory.TempFolder, root, claude)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
            bodies.Add(await posted.Content.ReadAsStringAsync());
        }

        using (HttpResponseMessage invalid = await Post(client, "{\"pollMinutes\":0}"))
        {
            bodies.Add(await invalid.Content.ReadAsStringAsync());
        }

        factory.Services.GetRequiredService<StatusStore>()
            .Update(status => status with { LastPoll = factory.Clock.GetUtcNow() });
        bodies.Add(await client.GetStringAsync("/api/status"));
        bodies.Add(await client.GetStringAsync(Route));

        Assert.Equal(roomKitBefore, File.ReadAllBytes(roomKit));
        using JsonDocument update = JsonDocument.Parse(await client.GetStringAsync(UpdateRoute));
        Assert.True(update.RootElement.GetProperty("autoUpdate").GetBoolean());
        bodies.Add(File.ReadAllText(SettingsFile(factory)));
        bodies.Add(File.ReadAllText(Path.Combine(factory.DataFolder, "status.json")));
        foreach (string body in bodies)
        {
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }

        Assert.Equal(rootBefore, TestFactoryRoot.Hash(root));
        Assert.Equal(
            ["access-token", "prwatch-settings.json", "settings.json", "status.json"],
            Directory.EnumerateFiles(factory.DataFolder).Select(Path.GetFileName)
                .Where(name => name is "access-token" or "prwatch-settings.json" or "settings.json" or "status.json")
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SettingsAndStatusFiles_AreCreatedAs0600()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes only; CI runs on macOS.
        }

        using PrWatchFactory factory = NewHost(out _, out _);
        using HttpClient client = await Start(factory);

        using HttpResponseMessage posted = await Post(client, "{\"pollMinutes\":8}");
        factory.Services.GetRequiredService<StatusStore>()
            .Update(status => status with { LastPoll = factory.Clock.GetUtcNow() });

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(privateMode, File.GetUnixFileMode(SettingsFile(factory)));
        Assert.Equal(privateMode, File.GetUnixFileMode(Path.Combine(factory.DataFolder, "status.json")));
        Assert.Empty(Directory.EnumerateFiles(factory.DataFolder, "*.tmp"));
    }

    private static PrWatchFactory NewHost(out string root, out string claude)
    {
        PrWatchFactory factory = new();
        root = TestFactoryRoot.Create(factory.TempFolder);
        claude = TestFactoryRoot.FakeClaude(factory.TempFolder);
        factory.FactoryRoot = root;
        return factory;
    }

    /// <summary>Starts the host and returns a token client.</summary>
    private static async Task<HttpClient> Start(PrWatchFactory factory)
    {
        using (HttpClient anonymous = factory.CreateClient())
        {
            using HttpResponseMessage health = await anonymous.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        return factory.CreateTokenClient();
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string body)
    {
        return client.PostAsync(Route, new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private static string SettingsFile(PrWatchFactory factory)
    {
        return Path.Combine(factory.DataFolder, "prwatch-settings.json");
    }

    private static string Fill(string template, string temp, string root, string claude)
    {
        return template
            .Replace("{temp}", Escape(temp), StringComparison.Ordinal)
            .Replace("{root}", Escape(root), StringComparison.Ordinal)
            .Replace("{claude}", Escape(claude), StringComparison.Ordinal);
    }

    private static string Escape(string path)
    {
        return JsonSerializer.Serialize(path)[1..^1];
    }

    private static JsonObject Defaults(string? root)
    {
        bool known = root is not null;
        return new JsonObject
        {
            ["enabled"] = false,
            ["factoryRoot"] = root,
            ["projects"] = null,
            ["pollMinutes"] = 5,
            ["quietMinutes"] = 10,
            ["autoReviewTrusted"] = true,
            ["autoMergeTrusted"] = false,
            ["reviewerMode"] = null,
            ["maxBudgetUsd"] = 2.0,
            ["brakeFiveHourPercent"] = 60,
            ["brakeSevenDayPercent"] = 80,
            ["brakeFiveHourUsd"] = 5.0,
            ["brakeSevenDayUsd"] = 25.0,
            ["brakeRunsPerHour"] = 3,
            ["claudePath"] = null,
            ["effectiveMode"] = known ? "quality" : null,
            ["effectiveModel"] = known ? "sonnet" : null,
            ["knownProjects"] = known ? new JsonArray("Alpha", "Beta", "factory") : new JsonArray(),
            ["knownModes"] = known ? new JsonArray("cheap", "quality") : new JsonArray(),
        };
    }

    /// <summary>Same keys in the same order, and equal values (numbers compared as numbers).</summary>
    private static void AssertJson(JsonNode expected, string actual)
    {
        JsonObject parsed = (JsonObject)JsonNode.Parse(actual)!;
        JsonObject wanted = (JsonObject)JsonNode.Parse(expected.ToJsonString())!;
        Assert.Equal(wanted.Select(pair => pair.Key), parsed.Select(pair => pair.Key));
        foreach (KeyValuePair<string, JsonNode?> pair in wanted)
        {
            JsonNode? value = parsed[pair.Key];
            if (pair.Value is JsonValue number && number.GetValueKind() == JsonValueKind.Number)
            {
                Assert.Equal(number.GetValue<double>(), value!.GetValue<double>());
            }
            else
            {
                Assert.True(JsonNode.DeepEquals(pair.Value, value), pair.Key + ": " + value?.ToJsonString());
            }
        }
    }
}
