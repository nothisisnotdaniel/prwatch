using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PRWatch.Tests;

/// <summary>The PR list view (job 426): <c>GET /api/prs</c> with its order, fields, runs and taps, and
/// <see cref="PrActions.For"/>. Each test writes its own <c>status.json</c> in its own temp folder.</summary>
public sealed class PrViewTests
{
    private const string Route = "/api/prs";

    private const string HeadA = WatcherRig.ShaA;

    private const string HeadB = WatcherRig.ShaB;

    private static readonly DateTimeOffset Now = FakeClock.DefaultNow;

    [Fact]
    public async Task Get_WithoutToken_Is401()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_OnAFreshHost_IsAnEmptyList_NoStore()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = await Start(factory);

        using HttpResponseMessage response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("{\"prs\":[]}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_ListsThePrsByProjectThenNumber_WithEveryField_AndOnlyTheirOwnRuns()
    {
        using PrWatchFactory factory = new();
        Status status = new()
        {
            LastPoll = Now,
            Prs =
            [
                Pr("Beta", 3, trusted: false),
                Pr("Alpha", 12, round: new RoundStatus
                {
                    K = 2, HeadSha = HeadA, Current = true, Clean = true, Tests = "pass", Mergeable = true,
                }) with { Stage = "review" },
                Pr("Alpha", 2, trusted: false) with { Request = "review" },
            ],
            Runs = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, RunStatus>
            {
                ["Alpha-12"] = new()
                {
                    HeadSha = HeadA,
                    StartedAt = Now.AddMinutes(-10),
                    EndedAt = Now.AddMinutes(-2),
                    ExitCode = 0,
                    Result = RunStatus.Ok,
                    CostUsd = 0.42,
                    IsError = false,
                },
                ["Gamma-9"] = new() { Result = RunStatus.Error },
                ["bad key"] = new() { Result = RunStatus.Error },
                ["x-0"] = new() { Result = RunStatus.Error },
            }),
        };
        WriteStatus(factory, status);
        using HttpClient client = await Start(factory);

        string body = await client.GetStringAsync(Route);

        Assert.DoesNotContain("Gamma", body, StringComparison.Ordinal);
        Assert.DoesNotContain("bad key", body, StringComparison.Ordinal);
        Assert.DoesNotContain("x-0", body, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement[] prs = [.. document.RootElement.GetProperty("prs").EnumerateArray()];
        Assert.Equal(["Alpha-2", "Alpha-12", "Beta-3"], prs.Select(pr =>
            pr.GetProperty("project").GetString() + "-" + pr.GetProperty("number").GetInt32()));
        Assert.Equal(
            "{\"project\":\"Alpha\",\"number\":12,\"title\":\"Title 12\",\"author\":\"someone\","
            + "\"url\":\"https://github.com/owner/repo/pull/12\",\"trusted\":true,\"headSha\":\"" + HeadA + "\","
            + "\"stage\":\"review\",\"request\":null,\"requestUnused\":false,"
            + "\"round\":{\"k\":2,\"current\":true,\"clean\":true,\"tests\":\"pass\",\"mergeable\":true},"
            + "\"run\":{\"startedAt\":\"2026-11-26T09:50:00.000Z\",\"endedAt\":\"2026-11-26T09:58:00.000Z\","
            + "\"result\":\"ok\",\"costUsd\":0.42},\"actions\":[\"merge\",\"cancel\"]}",
            prs[1].GetRawText());
        Assert.Equal(
            "{\"project\":\"Alpha\",\"number\":2,\"title\":\"Title 2\",\"author\":\"someone\","
            + "\"url\":\"https://github.com/owner/repo/pull/2\",\"trusted\":false,\"headSha\":\"" + HeadA + "\","
            + "\"stage\":null,\"request\":\"review\",\"requestUnused\":false,\"round\":null,\"run\":null,\"actions\":[]}",
            prs[0].GetRawText());
        Assert.Equal("[\"consent\"]", prs[2].GetProperty("actions").GetRawText());
    }

    [Fact]
    public void View_DropsARunKeyThatIsNoRunKey_EvenWhenTheStoreHoldsIt()
    {
        // The store's loader drops such keys already; the view checks again on its own.
        Status status = new()
        {
            Prs = [Pr("Alpha", 1)],
            Runs = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, RunStatus>
            {
                ["bad key"] = new() { Result = RunStatus.Error },
                ["x-0"] = new() { Result = RunStatus.Error },
                ["Alpha-2"] = new() { Result = RunStatus.Error },
            }),
        };

        JsonObject view = PrWatchApp.PrsView(status, new Settings());

        JsonNode pr = Assert.Single(view["prs"]!.AsArray())!;
        Assert.Null(pr["run"]);
    }

    [Fact]
    public async Task Get_HidesMergeOfATrustedCleanRound_WhileAutoMergeTrustedIsOn()
    {
        using PrWatchFactory factory = new();
        File.WriteAllText(Path.Combine(factory.CreateDataFolder(), SettingsStore.FileName),
            "{\"autoMergeTrusted\":true}");
        WriteStatus(factory, new Status
        {
            Prs = [Pr("Alpha", 1, round: new RoundStatus { K = 1, HeadSha = HeadA, Current = true, Clean = true })],
        });
        using HttpClient client = await Start(factory);

        using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync(Route));

        JsonElement pr = Assert.Single(document.RootElement.GetProperty("prs").EnumerateArray());
        Assert.Equal("[]", pr.GetProperty("actions").GetRawText());
    }

    // trusted, request, round (none | current-clean | current-dirty | old | current-clean-notests), stage,
    // autoMergeTrusted → expected taps
    [Theory]
    [InlineData(false, null, "none", null, false, "consent")]
    [InlineData(false, null, "old", null, false, "consent")]
    [InlineData(false, null, "current-clean-notests", null, false, "consent,merge")]
    [InlineData(false, null, "current-clean", null, false, "merge")]
    [InlineData(false, null, "current-clean", null, true, "merge")]
    [InlineData(false, null, "current-dirty", null, false, "")]
    [InlineData(false, null, "current-dirty", "review", false, "cancel")]
    [InlineData(false, null, "none", "review", false, "consent,cancel")]
    [InlineData(false, "review", "none", "review", false, "")]
    [InlineData(false, "merge", "current-clean", null, false, "")]
    [InlineData(false, "cancel", "old", "review", false, "")]
    [InlineData(true, null, "none", null, false, "")]
    [InlineData(true, null, "old", null, false, "")]
    [InlineData(true, null, "current-clean-notests", null, false, "merge")]
    [InlineData(true, null, "current-clean", null, false, "merge")]
    [InlineData(true, null, "current-clean", null, true, "")]
    [InlineData(true, null, "current-clean", "review", true, "cancel")]
    [InlineData(true, null, "current-dirty", "merged", false, "")]
    [InlineData(true, null, "old", "closed", false, "")]
    [InlineData(true, "review", "current-clean", "review", false, "")]
    public void For_GivesTheTapsThatApply(bool trusted, string? request, string round, string? stage,
        bool autoMergeTrusted, string expected)
    {
        RoundStatus? rounds = round switch
        {
            "none" => null,
            "current-clean" => new RoundStatus { K = 1, HeadSha = HeadA, Current = true, Clean = true, Tests = "pass" },
            "current-dirty" => new RoundStatus { K = 1, HeadSha = HeadA, Current = true, Clean = false, Tests = "fail" },
            "current-clean-notests" => new RoundStatus { K = 1, HeadSha = HeadA, Current = true, Clean = true },
            _ => new RoundStatus { K = 1, HeadSha = HeadB, Current = false, Clean = true, Tests = "pass" },
        };
        PrStatus pr = Pr("Alpha", 1, trusted, rounds) with { Request = request, Stage = stage };

        IReadOnlyList<string> actions = PrActions.For(pr, new Settings { AutoMergeTrusted = autoMergeTrusted });

        Assert.Equal(expected, string.Join(",", actions));
        Assert.All(actions, action => Assert.Contains(action, PrActions.All));
    }

    // --- job 449: a request a run already used ----------------------------------------------------

    [Theory]
    [InlineData(false, "review", "none", "review", false)]
    [InlineData(false, "merge", "current-clean", null, false)]
    [InlineData(false, "cancel", "old", "review", false)]
    [InlineData(false, "review", "current-clean-notests", null, false)]
    [InlineData(true, "review", "current-clean", "review", false)]
    [InlineData(true, "merge", "current-clean", null, true)]
    [InlineData(true, "review", "old", null, false)]
    public void For_AnUnusedRequest_GivesTheTapsOfThePrWithoutARequest(bool trusted, string request, string round,
        string? stage, bool autoMergeTrusted)
    {
        RoundStatus? rounds = round switch
        {
            "none" => null,
            "current-clean" => new RoundStatus { K = 1, HeadSha = HeadA, Current = true, Clean = true, Tests = "pass" },
            "current-clean-notests" => new RoundStatus { K = 1, HeadSha = HeadA, Current = true, Clean = true },
            _ => new RoundStatus { K = 1, HeadSha = HeadB, Current = false, Clean = true, Tests = "pass" },
        };
        PrStatus plain = Pr("Alpha", 1, trusted, rounds) with { Stage = stage };
        Settings settings = new() { AutoMergeTrusted = autoMergeTrusted };

        IReadOnlyList<string> waiting = PrActions.For(plain with { Request = request }, settings);
        IReadOnlyList<string> unused = PrActions.For(plain with { Request = request, RequestUnused = true }, settings);

        Assert.Empty(waiting);
        Assert.Equal(PrActions.For(plain, settings), unused);
    }

    [Fact]
    public async Task Get_ShowsRequestUnused_AndTheTapsOfAnUnusedRequest()
    {
        using PrWatchFactory factory = new();
        Status status = new()
        {
            LastPoll = Now,
            Prs =
            [
                Pr("Alpha", 1, trusted: false) with { Request = "review", RequestUnused = true },
                Pr("Alpha", 2, trusted: false) with { Request = "review" },
            ],
        };
        WriteStatus(factory, status);
        using HttpClient client = await Start(factory);

        using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync(Route));
        using JsonDocument full = JsonDocument.Parse(await client.GetStringAsync("/api/status"));

        JsonElement[] prs = [.. document.RootElement.GetProperty("prs").EnumerateArray()];
        Assert.True(prs[0].GetProperty("requestUnused").GetBoolean());
        Assert.Equal("[\"consent\"]", prs[0].GetProperty("actions").GetRawText());
        Assert.False(prs[1].GetProperty("requestUnused").GetBoolean());
        Assert.Equal("[]", prs[1].GetProperty("actions").GetRawText());
        JsonElement[] stored = [.. full.RootElement.GetProperty("prs").EnumerateArray()];
        Assert.Equal([true, false], stored.Select(pr => pr.GetProperty("requestUnused").GetBoolean()));
    }

    private static PrStatus Pr(string project, int number, bool trusted = true, RoundStatus? round = null)
    {
        return new PrStatus
        {
            Project = project,
            Number = number,
            Title = "Title " + number,
            Author = "someone",
            Url = "https://github.com/owner/repo/pull/" + number,
            HeadSha = HeadA,
            HeadSeenAt = Now.AddHours(-1),
            Trusted = trusted,
            Round = round,
        };
    }

    private static void WriteStatus(PrWatchFactory factory, Status status)
    {
        File.WriteAllBytes(Path.Combine(factory.CreateDataFolder(), "status.json"),
            JsonSerializer.SerializeToUtf8Bytes(status, PrWatchJson.FileOptions));
    }

    private static async Task<HttpClient> Start(PrWatchFactory factory)
    {
        using (HttpClient anonymous = factory.CreateClient())
        {
            using HttpResponseMessage health = await anonymous.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        return factory.CreateTokenClient();
    }
}
