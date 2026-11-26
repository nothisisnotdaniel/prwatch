using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PRWatch;

/// <summary>PRWatch's values for RoomKit's core: names, port, release repository and version, its services and
/// its routes. PRWatch has no factory module and no job format.</summary>
internal static class PrWatchApp
{
    public const string SettingsRoute = "/api/settings";

    public const string StatusRoute = "/api/status";

    /// <summary>The largest settings body a <c>POST</c> may send.</summary>
    public const int MaxSettingsBodyBytes = 16 * 1024;

    private const string JsonType = "application/json; charset=utf-8";

    /// <summary>The app's options. The version is this assembly's informational version (<c>dev</c> without
    /// <c>-p:Version</c>), never RoomKit's.</summary>
    public static RoomKitOptions Options { get; } = new()
    {
        AppName = "PRWatch",
        ShortName = "PRWatch",
        DataFolderName = "PRWatch",
        SolutionFileName = "PRWatch.slnx",
        DefaultPort = 8101,
        ReleaseRepository = "nothisisnotdaniel/prwatch",
        InformationalVersion = typeof(PrWatchApp).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    };

    /// <summary>The data folder: the folder of RoomKit's token file (config <c>TokenFile</c>, else
    /// <c>~/Library/Application Support/PRWatch</c>).</summary>
    public static string DataFolder(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? tokenFile = configuration["TokenFile"];
        if (!string.IsNullOrWhiteSpace(tokenFile))
        {
            return Path.GetDirectoryName(Path.GetFullPath(tokenFile))!;
        }

        string data = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return Path.Combine(data, Options.DataFolderName);
    }

    /// <summary>Registers the status and settings stores (both in the data folder, clock from the container),
    /// loads the settings at start, so an unreadable file shows in the status at once, and adds the watcher with
    /// its poll runner and the review runner (<see cref="ClaudeReviewRunner"/> with the config value
    /// <c>RunTimeoutSeconds</c>, unless one is already registered), which is stopped with the app.</summary>
    public static void AddServices(RoomServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        string folder = DataFolder(services.Configuration);
        string? factoryRoot = services.Configuration["FactoryRoot"];
        services.Services.AddSingleton(provider => new StatusStore(folder, provider.GetRequiredService<TimeProvider>()));
        services.Services.AddSingleton(provider => new SettingsStore(
            folder, factoryRoot, provider.GetRequiredService<StatusStore>()));
        services.Services.AddHostedService<SettingsLoader>();
        services.Services.AddSingleton(_ => PollRunner.FromConfiguration(services.Configuration));
        services.Services.TryAddSingleton<IReviewRunner>(provider => new ClaudeReviewRunner(
            provider.GetRequiredService<SettingsStore>(),
            provider.GetRequiredService<StatusStore>(),
            provider.GetRequiredService<TimeProvider>(),
            ClaudeReviewRunner.TimeoutFrom(services.Configuration)));
        // Hosted services stop in reverse order: the watcher first, then the running claude run.
        services.Services.AddHostedService<RunnerStopper>();
        services.Services.AddSingleton<Watcher>();
        services.Services.AddHostedService(provider => provider.GetRequiredService<Watcher>());
    }

    /// <summary>Maps <c>GET</c>/<c>POST /api/settings</c> and <c>GET /api/status</c>.</summary>
    public static void Map(RoomRoutes routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.App.MapGet(SettingsRoute, (HttpContext context, SettingsStore settings) =>
            Json(context, SettingsStore.View(settings.Current).ToJsonString(), StatusCodes.Status200OK));
        routes.MapPost(SettingsRoute, ChangeSettings);
        routes.App.MapGet(StatusRoute, (HttpContext context, StatusStore status) =>
            Json(context, JsonSerializer.Serialize(status.Current, PrWatchJson.Options), StatusCodes.Status200OK));
    }

    private static async Task<IResult> ChangeSettings(HttpContext context, SettingsStore settings, StatusStore status)
    {
        byte[]? body = await ReadCapped(context.Request, MaxSettingsBodyBytes);
        if (body is null)
        {
            return Invalid(context, []);
        }

        SettingsChange change;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            change = settings.Change(document.RootElement);
        }
        catch (JsonException)
        {
            return Invalid(context, []);
        }

        if (change.SaveFailed)
        {
            return Json(context, "{\"error\":\"save_failed\"}", StatusCodes.Status500InternalServerError);
        }

        if (change.Saved is null)
        {
            return Invalid(context, change.Fields);
        }

        // A saved change is the user's way back from "needs login": the next run checks the login again.
        status.Update(previous => previous with { Login = previous.Login with { State = LoginStatus.Unknown } });
        return Json(context, SettingsStore.View(change.Saved).ToJsonString(), StatusCodes.Status200OK);
    }

    private static IResult Invalid(HttpContext context, IReadOnlyList<string> fields)
    {
        JsonObject error = new()
        {
            ["error"] = "invalid_settings",
            ["fields"] = new JsonArray([.. fields.Select(field => (JsonNode?)JsonValue.Create(field))]),
        };
        return Json(context, error.ToJsonString(), StatusCodes.Status400BadRequest);
    }

    private static IResult Json(HttpContext context, string body, int status)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Text(body, JsonType, statusCode: status);
    }

    /// <summary>The body when it is at most <paramref name="max"/> bytes, else <c>null</c>.</summary>
    private static async Task<byte[]?> ReadCapped(HttpRequest request, int max)
    {
        if (request.ContentLength > max)
        {
            return null;
        }

        using MemoryStream buffer = new();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Builds the settings store when the host starts.</summary>
    private sealed class SettingsLoader : IHostedService
    {
        private readonly IServiceProvider services;

        public SettingsLoader(IServiceProvider services)
        {
            this.services = services;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            services.GetRequiredService<SettingsStore>();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>Stops the <see cref="ClaudeReviewRunner"/> (kills a running run, recorded as <c>stopped</c>)
    /// when the host stops.</summary>
    private sealed class RunnerStopper : IHostedService
    {
        private readonly IReviewRunner runner;

        public RunnerStopper(IReviewRunner runner)
        {
            this.runner = runner;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return runner is ClaudeReviewRunner claude ? claude.StopAsync(cancellationToken) : Task.CompletedTask;
        }
    }
}
