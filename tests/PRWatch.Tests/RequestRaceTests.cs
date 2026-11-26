using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace PRWatch.Tests;

/// <summary>A request written while a <c>job.py pr poll</c> runs (story 022): a poll that started before the
/// write still shows the request for the same head, never for another head, and every poll that starts after the
/// write decides the value itself. Each test has its own host, temp folder, factory root, fake <c>job.py</c> (held
/// through its <c>poll-wait</c> gate), fake runner and clock; <c>enabled</c> stays off, so the only polls are the
/// ones the test starts.</summary>
[Collection("RequestWriter")]
public sealed class RequestRaceTests
{
    private const string Sha = WatcherRig.ShaA;

    public static TheoryData<string> Actions => new() { "review", "merge", "cancel" };

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task PollStartedBeforeTheWrite_SameHead_ShowsTheRequest(string action)
    {
        using Rig rig = await Rig.Start();

        await rig.HeldPoll(async () => await rig.PostOk(action, 7));

        Assert.Equal(action, await rig.StatusRequest(7));
        Assert.Equal(action, await rig.PrsRequest(7));
        Assert.Null(await rig.StatusRequest(8));
        Assert.Null(await rig.PrsRequest(8));
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task PollStartedBeforeTheWrite_NewHead_ShowsNoRequest(string action)
    {
        using Rig rig = await Rig.Start();
        rig.Output(WatcherRig.ShaB);

        await rig.HeldPoll(async () => await rig.PostOk(action, 7));

        Assert.Equal(WatcherRig.ShaB, await rig.StatusHead(7));
        Assert.Null(await rig.StatusRequest(7));
        Assert.Null(await rig.PrsRequest(7));
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task PollStartedAfterTheWrite_DecidesTheValue(string action)
    {
        using Rig rig = await Rig.Start();
        await rig.HeldPoll(async () => await rig.PostOk(action, 7));
        Assert.Equal(action, await rig.StatusRequest(7));
        FakeJobPy.Unhold(rig.Root);

        await rig.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Null(await rig.StatusRequest(7));
        Assert.Null(await rig.PrsRequest(7));

        await rig.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Null(await rig.StatusRequest(7));
        Assert.Null(await rig.PrsRequest(7));
    }

    [Fact]
    public async Task PollStartedAfterTheWrite_WithoutAHeldPoll_DecidesTheValue()
    {
        using Rig rig = await Rig.Start();
        await rig.PostOk("merge", 7);
        Assert.Equal("merge", await rig.StatusRequest(7));

        await rig.Watcher.PollOnceAsync(CancellationToken.None);

        Assert.Null(await rig.StatusRequest(7));
    }

    [Fact]
    public async Task PollStartedBeforeTheWrite_PrGone_IsNotListedAgain()
    {
        using Rig rig = await Rig.Start();
        JsonArray prs = [WatcherRig.Pr("Alpha", 8, Sha, trusted: false)];
        FakeJobPy.SetOutput(rig.Root, new JsonObject { ["prs"] = prs, ["skipped"] = new JsonArray() }.ToJsonString());

        await rig.HeldPoll(async () => await rig.PostOk("merge", 7));

        Assert.False(await rig.Lists("/api/status", 7));
        Assert.False(await rig.Lists("/api/prs", 7));
        Assert.Null(await rig.StatusRequest(8));
    }

    [Fact]
    public async Task TwoTapsDuringOneHeldPoll_TheLatestWins()
    {
        using Rig rig = await Rig.Start();

        await rig.HeldPoll(async () =>
        {
            await rig.PostOk("review", 7);
            await rig.PostOk("merge", 7);
        });

        Assert.Equal("merge", await rig.StatusRequest(7));
        Assert.Equal("merge", await rig.PrsRequest(7));
        Assert.Null(await rig.StatusRequest(8));
    }

    private sealed class Rig : IDisposable
    {
        private Rig(PrWatchFactory factory, string root)
        {
            Factory = factory;
            Root = root;
        }

        public PrWatchFactory Factory { get; }

        public string Root { get; }

        public HttpClient Client { get; private set; } = null!;

        public Watcher Watcher => Factory.Services.GetRequiredService<Watcher>();

        private StatusStore Status => Factory.Services.GetRequiredService<StatusStore>();

        /// <summary>A host whose status lists Alpha#7 and Alpha#8 on <see cref="Sha"/>, and whose fake
        /// <c>job.py</c> prints both on that head with <c>request: null</c>.</summary>
        public static async Task<Rig> Start()
        {
            PrWatchFactory factory = new();
            string root = TestFactoryRoot.Create(factory.TempFolder);
            FakeJobPy.Write(root, Path.Combine(factory.TempFolder, "poll-calls.jsonl"));
            factory.FactoryRoot = root;
            factory.TestServices = services => services.AddSingleton<IReviewRunner>(new FakeReviewRunner());
            Rig rig = new(factory, root);
            rig.Output(Sha);
            rig.Client = factory.CreateTokenClient();
            using HttpResponseMessage health = await rig.Client.GetAsync("/health");
            rig.Seed(7);
            rig.Seed(8);
            return rig;
        }

        /// <summary>The poll output: Alpha#7 on <paramref name="head7"/>, Alpha#8 on <see cref="Sha"/>, both
        /// with <c>request: null</c>.</summary>
        public void Output(string head7)
        {
            JsonArray prs = [WatcherRig.Pr("Alpha", 7, head7, trusted: false), WatcherRig.Pr("Alpha", 8, Sha, trusted: false)];
            FakeJobPy.SetOutput(Root, new JsonObject { ["prs"] = prs, ["skipped"] = new JsonArray() }.ToJsonString());
        }

        /// <summary>Starts a poll held by the fake's gate, waits until the fake has started, runs
        /// <paramref name="during"/>, releases the fake and awaits the poll.</summary>
        public async Task HeldPoll(Func<Task> during)
        {
            FakeJobPy.Hold(Root);
            Task polling = Watcher.PollOnceAsync(CancellationToken.None);
            await WatcherRig.WaitFor(() => Task.FromResult(FakeJobPy.Started(Root) || polling.IsCompleted),
                "the held poll to start");
            Assert.False(polling.IsCompleted, "the poll ended before it was released");
            await during();
            FakeJobPy.Release(Root);
            await polling.WaitAsync(WatcherRig.Bound);
        }

        public async Task PostOk(string action, int number)
        {
            JsonObject body = new() { ["project"] = "Alpha", ["number"] = number, ["headSha"] = Sha };
            if (action == "review")
            {
                body["consent"] = true;
            }

            using HttpResponseMessage response = await Client.PostAsync(
                "/api/prs/" + action, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task<string?> StatusRequest(int number)
        {
            return (await Find("/api/status", number)).GetProperty("request").GetString();
        }

        public async Task<string?> StatusHead(int number)
        {
            return (await Find("/api/status", number)).GetProperty("headSha").GetString();
        }

        public async Task<string?> PrsRequest(int number)
        {
            return (await Find("/api/prs", number)).GetProperty("request").GetString();
        }

        public async Task<bool> Lists(string route, int number)
        {
            using JsonDocument document = JsonDocument.Parse(await Client.GetStringAsync(route));
            return document.RootElement.GetProperty("prs").EnumerateArray()
                .Any(pr => pr.GetProperty("project").GetString() == "Alpha"
                    && pr.GetProperty("number").GetInt32() == number);
        }

        public void Dispose()
        {
            Client?.Dispose();
            Factory.Dispose();
        }

        private void Seed(int number)
        {
            Status.Update(status => status with
            {
                Prs = [.. status.Prs, new PrStatus
                {
                    Project = "Alpha",
                    Number = number,
                    Title = "PR " + number,
                    Author = "someone",
                    HeadSha = Sha,
                    HeadSeenAt = Factory.Clock.GetUtcNow(),
                    Trusted = false,
                }],
            });
        }

        private async Task<JsonElement> Find(string route, int number)
        {
            using JsonDocument document = JsonDocument.Parse(await Client.GetStringAsync(route));
            return document.RootElement.GetProperty("prs").EnumerateArray()
                .Single(pr => pr.GetProperty("project").GetString() == "Alpha"
                    && pr.GetProperty("number").GetInt32() == number)
                .Clone();
        }
    }
}
