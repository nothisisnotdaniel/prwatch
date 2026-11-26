using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PRWatch.Tests;

/// <summary>The Consent, Merge and Cancel requests (backlog item 6): <c>POST /api/prs/review</c>,
/// <c>/api/prs/merge</c> and <c>/api/prs/cancel</c>, the checks against the last poll and the atomic, link-free
/// write of <c>factory/prs/&lt;repo&gt;-&lt;n&gt;/request.json</c>. Each test has its own temp folder, factory root,
/// fake <c>job.py</c>, fake runner and clock.</summary>
public sealed class RequestTests
{
    private const string Sha = WatcherRig.ShaA;

    private const string Title = "SEEDED-TITLE-q81";

    private const string Author = "SEEDED-AUTHOR-q81";

    private const UnixFileMode Mode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode Mode700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static TheoryData<string> Actions => new() { "review", "merge", "cancel" };

    // --- AC-1: a valid request -------------------------------------------------------------------

    [Theory]
    [InlineData("review", "Alpha", true)]
    [InlineData("review", "Alpha", false)]
    [InlineData("merge", "Alpha", false)]
    [InlineData("cancel", "Alpha", false)]
    [InlineData("merge", "factory", false)]
    public async Task Valid_Returns200_AndWritesExactlyActionHeadAndConsent_As0600(
        string action, string project, bool consent)
    {
        using Rig rig = await Rig.Start();
        rig.SeedPr(project, 7, Sha);

        using HttpResponseMessage response = await rig.Post(action, Body(action, project, 7, Sha, consent));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["action", "project", "number", "headSha"],
            answer.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(action, answer.RootElement.GetProperty("action").GetString());
        Assert.Equal(project, answer.RootElement.GetProperty("project").GetString());
        Assert.Equal(7, answer.RootElement.GetProperty("number").GetInt32());
        Assert.Equal(Sha, answer.RootElement.GetProperty("headSha").GetString());
        string file = Path.Combine(rig.Root, "factory", "prs", project + "-7", "request.json");
        AssertRequestFile(file, action, Sha, action == "review" && consent);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Valid_ReplacesAnExistingFile_AndMakesIt0600(string action)
    {
        using Rig rig = await Rig.Start();
        rig.SeedPr("Alpha", 3, Sha);
        string folder = Path.Combine(rig.Root, "factory", "prs", "Alpha-3");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "request.json");
        File.WriteAllText(file, "{\"action\":\"old\",\"pad\":\"" + new string('x', 5000) + "\"}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, Mode600 | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        using HttpResponseMessage first = await rig.Post(action, Body(action, "Alpha", 3, Sha, true));
        using HttpResponseMessage second = await rig.Post(action, Body(action, "Alpha", 3, Sha, false));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        AssertRequestFile(file, action, Sha, consent: false);
        Assert.Equal(["request.json"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    // --- AC-2: invalid bodies --------------------------------------------------------------------

    [Theory]
    [InlineData("review", "big")]
    [InlineData("review", "[]")]
    [InlineData("review", "\"text\"")]
    [InlineData("review", "{not json")]
    [InlineData("review", "")]
    [InlineData("review", "extra-key")]
    [InlineData("review", "duplicate-key")]
    [InlineData("review", "unknown-project")]
    [InlineData("review", "not-a-project-folder")]
    [InlineData("review", "project-number")]
    [InlineData("review", "project-missing")]
    [InlineData("review", "number-zero")]
    [InlineData("review", "number-negative")]
    [InlineData("review", "number-too-big")]
    [InlineData("review", "number-fraction")]
    [InlineData("review", "number-exponent")]
    [InlineData("review", "number-string")]
    [InlineData("review", "number-missing")]
    [InlineData("review", "sha-upper")]
    [InlineData("review", "sha-short")]
    [InlineData("review", "sha-number")]
    [InlineData("review", "sha-missing")]
    [InlineData("review", "consent-missing")]
    [InlineData("review", "consent-string")]
    [InlineData("review", "consent-null")]
    [InlineData("merge", "consent-present")]
    [InlineData("cancel", "consent-present")]
    [InlineData("merge", "extra-key")]
    [InlineData("cancel", "sha-upper")]
    public async Task Invalid_Is400InvalidRequest_AndWritesNothing(string action, string rule)
    {
        using Rig rig = await Rig.Start();
        rig.SeedPr("Alpha", 1, Sha);
        string sha = "\"" + Sha + "\"";
        string consent = action == "review" ? ",\"consent\":true" : "";
        string body = rule switch
        {
            "big" => "{\"project\":\"Alpha\",\"number\":1,\"headSha\":" + sha + consent + new string(' ', 1100) + "}",
            "[]" or "\"text\"" or "{not json" or "" => rule,
            "extra-key" => "{\"project\":\"Alpha\",\"number\":1,\"headSha\":" + sha + consent + ",\"x\":1}",
            "duplicate-key" => "{\"project\":\"Alpha\",\"project\":\"Alpha\",\"number\":1,\"headSha\":" + sha + consent + "}",
            "unknown-project" => "{\"project\":\"Gamma\",\"number\":1,\"headSha\":" + sha + consent + "}",
            "not-a-project-folder" => "{\"project\":\"NotAProject\",\"number\":1,\"headSha\":" + sha + consent + "}",
            "project-number" => "{\"project\":1,\"number\":1,\"headSha\":" + sha + consent + "}",
            "project-missing" => "{\"number\":1,\"headSha\":" + sha + consent + "}",
            "number-zero" => "{\"project\":\"Alpha\",\"number\":0,\"headSha\":" + sha + consent + "}",
            "number-negative" => "{\"project\":\"Alpha\",\"number\":-1,\"headSha\":" + sha + consent + "}",
            "number-too-big" => "{\"project\":\"Alpha\",\"number\":1000000000,\"headSha\":" + sha + consent + "}",
            "number-fraction" => "{\"project\":\"Alpha\",\"number\":1.0,\"headSha\":" + sha + consent + "}",
            "number-exponent" => "{\"project\":\"Alpha\",\"number\":1e0,\"headSha\":" + sha + consent + "}",
            "number-string" => "{\"project\":\"Alpha\",\"number\":\"1\",\"headSha\":" + sha + consent + "}",
            "number-missing" => "{\"project\":\"Alpha\",\"headSha\":" + sha + consent + "}",
            "sha-upper" => "{\"project\":\"Alpha\",\"number\":1,\"headSha\":\"" + Sha.ToUpperInvariant() + "\"" + consent + "}",
            "sha-short" => "{\"project\":\"Alpha\",\"number\":1,\"headSha\":\"" + Sha[..39] + "\"" + consent + "}",
            "sha-number" => "{\"project\":\"Alpha\",\"number\":1,\"headSha\":1" + consent + "}",
            "sha-missing" => "{\"project\":\"Alpha\",\"number\":1" + consent + "}",
            "consent-missing" => "{\"project\":\"Alpha\",\"number\":1,\"headSha\":" + sha + "}",
            "consent-string" => "{\"project\":\"Alpha\",\"number\":1,\"headSha\":" + sha + ",\"consent\":\"true\"}",
            "consent-null" => "{\"project\":\"Alpha\",\"number\":1,\"headSha\":" + sha + ",\"consent\":null}",
            "consent-present" => "{\"project\":\"Alpha\",\"number\":1,\"headSha\":" + sha + ",\"consent\":false}",
            _ => throw new ArgumentException(rule),
        };

        using HttpResponseMessage response = await rig.Post(action, body);

        await AssertError(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "factory", "prs")));
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task WithoutAValidFactoryRoot_Is400InvalidRequest(string action)
    {
        using Rig rig = await Rig.Start(withRoot: false);
        rig.SeedPr("Alpha", 1, Sha);

        using HttpResponseMessage response = await rig.Post(action, Body(action, "Alpha", 1, Sha, true));

        await AssertError(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "factory", "prs")));
    }

    // --- AC-3: unknown and stale -----------------------------------------------------------------

    [Theory]
    [InlineData("review", "Alpha", 2, Sha, HttpStatusCode.NotFound, "unknown_pr")]
    [InlineData("merge", "Beta", 1, Sha, HttpStatusCode.NotFound, "unknown_pr")]
    [InlineData("cancel", "factory", 1, Sha, HttpStatusCode.NotFound, "unknown_pr")]
    [InlineData("review", "Alpha", 1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", HttpStatusCode.Conflict, "stale_request")]
    [InlineData("merge", "Alpha", 1, WatcherRig.ShaB, HttpStatusCode.Conflict, "stale_request")]
    [InlineData("cancel", "Alpha", 1, WatcherRig.ShaB, HttpStatusCode.Conflict, "stale_request")]
    public async Task UnknownOrStale_GivesTheFixedCode_AndWritesNothing(
        string action, string project, int number, string head, HttpStatusCode code, string error)
    {
        using Rig rig = await Rig.Start();
        rig.SeedPr("Alpha", 1, Sha);

        using HttpResponseMessage response = await rig.Post(action, Body(action, project, number, head, true));

        await AssertError(response, code, error);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "factory", "prs")));
        Assert.Null(Assert.Single(rig.Status.Current.Prs).Request);
    }

    // --- AC-4: links and the write ---------------------------------------------------------------

    [Theory]
    [InlineData("factory")]
    [InlineData("prs")]
    [InlineData("folder")]
    [InlineData("file")]
    public async Task ASymlinkOnThePath_Is500WriteFailed_AndLeavesItsTargetAlone(string place)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string folder = Path.Combine(Path.GetTempPath(), "prwatch-request-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(folder, "outside");
        Directory.CreateDirectory(outside);
        string root = TestFactoryRoot.Create(folder);
        string factoryDir = Path.Combine(root, "factory");
        string prs = Path.Combine(factoryDir, "prs");
        string target;
        switch (place)
        {
            case "factory":
                target = Path.Combine(outside, "factory");
                Directory.Move(factoryDir, target);
                Directory.CreateSymbolicLink(factoryDir, target);
                break;
            case "prs":
                target = Path.Combine(outside, "prs");
                Directory.CreateDirectory(target);
                Directory.CreateSymbolicLink(prs, target);
                break;
            case "folder":
                target = Path.Combine(outside, "Alpha-1");
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(prs);
                Directory.CreateSymbolicLink(Path.Combine(prs, "Alpha-1"), target);
                break;
            default:
                target = Path.Combine(outside, "target.json");
                File.WriteAllText(target, "{\"keep\":true}");
                Directory.CreateDirectory(Path.Combine(prs, "Alpha-1"));
                File.CreateSymbolicLink(Path.Combine(prs, "Alpha-1", "request.json"), target);
                break;
        }

        try
        {
            using Rig rig = await Rig.Start(folder: folder, root: root);
            rig.SeedPr("Alpha", 1, Sha);
            string hash = TestFactoryRoot.Hash(outside);

            using HttpResponseMessage response = await rig.Post("review", Body("review", "Alpha", 1, Sha, true));

            await AssertError(response, HttpStatusCode.InternalServerError, "write_failed");
            Assert.Equal(hash, TestFactoryRoot.Hash(outside));
            Assert.Empty(Directory.EnumerateFiles(outside, "request.json", SearchOption.AllDirectories));
            Assert.Null(Assert.Single(rig.Status.Current.Prs).Request);
            if (place == "file")
            {
                Assert.Equal("{\"keep\":true}", File.ReadAllText(target));
                Assert.NotNull(new FileInfo(Path.Combine(prs, "Alpha-1", "request.json")).LinkTarget);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFolders_AreCreatedAs0700_WithNoTempFileLeft()
    {
        using Rig rig = await Rig.Start();
        rig.SeedPr("Beta", 12, Sha);
        string prs = Path.Combine(rig.Root, "factory", "prs");
        Assert.False(Directory.Exists(prs));

        using HttpResponseMessage response = await rig.Post("cancel", Body("cancel", "Beta", 12, Sha, false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Beta-12"], Directory.GetFileSystemEntries(prs).Select(Path.GetFileName));
        Assert.Equal(["request.json"], Directory.GetFileSystemEntries(Path.Combine(prs, "Beta-12")).Select(Path.GetFileName));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Mode700, File.GetUnixFileMode(prs));
            Assert.Equal(Mode700, File.GetUnixFileMode(Path.Combine(prs, "Beta-12")));
        }
    }

    [Fact]
    public async Task AFailingWrite_Is500WriteFailed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using Rig rig = await Rig.Start();
        rig.SeedPr("Alpha", 1, Sha);
        string folder = Path.Combine(rig.Root, "factory", "prs", "Alpha-1");
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using HttpResponseMessage response = await rig.Post("merge", Body("merge", "Alpha", 1, Sha, false));

            await AssertError(response, HttpStatusCode.InternalServerError, "write_failed");
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }
        finally
        {
            File.SetUnixFileMode(folder, Mode700);
        }
    }

    [Theory]
    [InlineData("a/../../x")]
    [InlineData("../Alpha")]
    [InlineData("Alpha/sub")]
    public void Write_RefusesAFolderNotDirectlyInsidePrs(string project)
    {
        string folder = Path.Combine(Path.GetTempPath(), "prwatch-request-" + Guid.NewGuid().ToString("N"));
        try
        {
            string root = TestFactoryRoot.Create(folder);
            string hash = TestFactoryRoot.Hash(folder);

            bool written = RequestWriter.Write(root, new PrRequest("review", project, 1, Sha, true));

            Assert.False(written);
            Assert.Equal(hash, TestFactoryRoot.Hash(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // --- AC-5: status, PollSoon, no untrusted text -----------------------------------------------

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Success_SetsTheRequestInTheStatus_AtOnce(string action)
    {
        using Rig rig = await Rig.Start();
        rig.SeedPr("Alpha", 1, Sha);
        rig.SeedPr("Alpha", 2, Sha);

        using HttpResponseMessage response = await rig.Post(action, Body(action, "Alpha", 1, Sha, true));
        string status = await rig.Client.GetStringAsync("/api/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(status);
        JsonElement[] prs = [.. document.RootElement.GetProperty("prs").EnumerateArray()];
        Assert.Equal(action, prs[0].GetProperty("request").GetString());
        Assert.Equal(JsonValueKind.Null, prs[1].GetProperty("request").ValueKind);
        Assert.False(File.Exists(rig.CallsFile));
    }

    [Fact]
    public async Task Success_WhileEnabled_PollsOnceAtOnce()
    {
        using Rig rig = await Rig.Start(enabled: true);
        await WatcherRig.WaitFor(() => Task.FromResult(FakeJobPy.Calls(rig.CallsFile).Count == 1), "the first poll");
        await WatcherRig.WaitFor(
            () => Task.FromResult(rig.Status.Current.LastPoll == rig.Factory.Clock.GetUtcNow()), "the first poll's status");
        Assert.Single(rig.Status.Current.Prs);

        using HttpResponseMessage response = await rig.Post("merge", Body("merge", "Alpha", 1, Sha, false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WatcherRig.WaitFor(() => Task.FromResult(FakeJobPy.Calls(rig.CallsFile).Count >= 2), "the second poll");
        Assert.Equal(2, FakeJobPy.Calls(rig.CallsFile).Count);
    }

    [Fact]
    public async Task Responses_AndTheLog_NeverHoldTheTitleOrAuthor()
    {
        using Rig rig = await Rig.Start();
        rig.SeedPr("Alpha", 1, Sha);
        List<string> bodies = [];
        foreach ((string action, string body) in new[]
        {
            ("review", Body("review", "Alpha", 1, Sha, true)),
            ("merge", Body("merge", "Alpha", 1, WatcherRig.ShaB, false)),
            ("cancel", Body("cancel", "Alpha", 9, Sha, false)),
            ("review", "{bad"),
        })
        {
            using HttpResponseMessage response = await rig.Post(action, body);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        foreach (string text in bodies.Concat(rig.Log.Lines))
        {
            Assert.DoesNotContain(Title, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Author, text, StringComparison.Ordinal);
        }
    }

    // --- AC-7: the token -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task WithoutTheToken_Is401_AndWritesNothing_AndGetIsNoWrite(string action)
    {
        using Rig rig = await Rig.Start();
        rig.SeedPr("Alpha", 1, Sha);
        using HttpClient anonymous = rig.Factory.CreateClient();

        using HttpResponseMessage post = await anonymous.PostAsync(
            "/api/prs/" + action, new StringContent(Body(action, "Alpha", 1, Sha, true), Encoding.UTF8, "application/json"));
        using HttpResponseMessage get = await rig.Client.GetAsync("/api/prs/" + action);

        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(RoomKitApp.UnauthorizedBody, await post.Content.ReadAsStringAsync());
        Assert.NotEqual(HttpStatusCode.OK, get.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "factory", "prs")));
        Assert.Null(Assert.Single(rig.Status.Current.Prs).Request);
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static string Body(string action, string project, int number, string head, bool consent)
    {
        JsonObject body = new() { ["project"] = project, ["number"] = number, ["headSha"] = head };
        if (action == "review")
        {
            body["consent"] = consent;
        }

        return body.ToJsonString();
    }

    private static void AssertRequestFile(string file, string action, string head, bool consent)
    {
        byte[] bytes = File.ReadAllBytes(file);
        Assert.True(bytes.Length <= RequestWriter.MaxFileBytes);
        using JsonDocument document = JsonDocument.Parse(bytes);
        Assert.Equal(["action", "headSha", "consent"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(action, document.RootElement.GetProperty("action").GetString());
        Assert.Equal(head, document.RootElement.GetProperty("headSha").GetString());
        Assert.Equal(consent, document.RootElement.GetProperty("consent").GetBoolean());
        Assert.Null(new FileInfo(file).LinkTarget);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Mode600, File.GetUnixFileMode(file));
        }
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode code, string error)
    {
        Assert.Equal(code, response.StatusCode);
        Assert.Equal("{\"error\":\"" + error + "\"}", await response.Content.ReadAsStringAsync());
    }

    /// <summary>A host on its own temp folder with a factory root (passed as <c>--FactoryRoot</c> unless
    /// <c>withRoot</c> is off), the fake <c>job.py</c> (its output: PR Alpha-1 at <see cref="Sha"/>), a fake
    /// runner and a log that keeps every line.</summary>
    private sealed class Rig : IDisposable
    {
        private Rig(PrWatchFactory factory, string root, string callsFile)
        {
            Factory = factory;
            Root = root;
            CallsFile = callsFile;
        }

        public PrWatchFactory Factory { get; }

        public string Root { get; }

        public string CallsFile { get; }

        public CapturingLog Log { get; } = new();

        public HttpClient Client { get; private set; } = null!;

        public StatusStore Status => Factory.Services.GetRequiredService<StatusStore>();

        public static async Task<Rig> Start(bool withRoot = true, bool enabled = false, string? folder = null,
            string? root = null)
        {
            PrWatchFactory factory = folder is null ? new PrWatchFactory() : new PrWatchFactory(folder, ownsFolder: false);
            string factoryRoot = root ?? TestFactoryRoot.Create(factory.TempFolder);
            string calls = Path.Combine(factory.TempFolder, "poll-calls.jsonl");
            FakeJobPy.Write(factoryRoot, calls);
            JsonObject pr = WatcherRig.Pr("Alpha", 1);
            pr["title"] = Title;
            pr["author"] = Author;
            FakeJobPy.SetOutput(factoryRoot, "{\"prs\":[" + pr.ToJsonString() + "],\"skipped\":[]}");
            Rig rig = new(factory, factoryRoot, calls);
            if (withRoot)
            {
                factory.FactoryRoot = factoryRoot;
            }

            if (enabled)
            {
                JsonObject settings = new()
                {
                    ["enabled"] = true,
                    ["factoryRoot"] = factoryRoot,
                    ["claudePath"] = TestFactoryRoot.FakeClaude(factory.TempFolder),
                };
                File.WriteAllText(Path.Combine(factory.CreateDataFolder(), SettingsStore.FileName), settings.ToJsonString());
            }

            factory.TestServices = services =>
            {
                services.AddSingleton<IReviewRunner>(new FakeReviewRunner());
                services.AddSingleton<ILoggerProvider>(rig.Log);
            };
            rig.Client = factory.CreateTokenClient();
            using HttpResponseMessage health = await rig.Client.GetAsync("/health");
            return rig;
        }

        /// <summary>Adds an open PR to the status as a poll would, with the seeded title and author.</summary>
        public void SeedPr(string project, int number, string head)
        {
            Status.Update(status => status with
            {
                Prs = [.. status.Prs, new PrStatus
                {
                    Project = project,
                    Number = number,
                    Title = Title,
                    Author = Author,
                    HeadSha = head,
                    HeadSeenAt = Factory.Clock.GetUtcNow(),
                    Trusted = false,
                }],
            });
        }

        public Task<HttpResponseMessage> Post(string action, string body)
        {
            return Client.PostAsync("/api/prs/" + action, new StringContent(body, Encoding.UTF8, "application/json"));
        }

        public void Dispose()
        {
            Client?.Dispose();
            Factory.Dispose();
        }
    }

    /// <summary>A logger provider that keeps every formatted line (and exception text) of the host.</summary>
    internal sealed class CapturingLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();

        public IReadOnlyCollection<string> Lines => lines;

        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(lines);
        }

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            private readonly ConcurrentQueue<string> lines;

            public Logger(ConcurrentQueue<string> lines)
            {
                this.lines = lines;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue(formatter(state, exception) + " " + exception);
            }
        }
    }
}
