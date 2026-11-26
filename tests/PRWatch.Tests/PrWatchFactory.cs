using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace PRWatch.Tests;

/// <summary>Hosts PRWatch in memory (no real port) with its own temp folder, deleted on dispose. The host gets
/// <c>--TokenFile=&lt;temp&gt;/PRWatch/access-token</c> and <c>--environment=Production</c>, so RoomKit's data
/// folder (token, push and update settings) is the temp one, never <c>~/Library/Application Support/PRWatch</c>.
/// Clients send <c>Host: 127.0.0.1:8101</c>. The clock is <see cref="Clock"/> (a fixed fake) and
/// <see cref="FactoryRoot"/>, when set, is passed as <c>--FactoryRoot</c>.</summary>
internal sealed class PrWatchFactory : WebApplicationFactory<Program>
{
    public const string Environment = "Production";

    public const string LoopbackBase = "http://127.0.0.1:8101/";

    private readonly bool ownsFolder;

    public PrWatchFactory()
        : this(Path.Combine(Path.GetTempPath(), "prwatch-test-" + Guid.NewGuid().ToString("N")), ownsFolder: true)
    {
    }

    /// <summary>A host on <paramref name="tempFolder"/>; the folder is deleted on dispose only when
    /// <paramref name="ownsFolder"/>.</summary>
    public PrWatchFactory(string tempFolder, bool ownsFolder)
    {
        TempFolder = tempFolder;
        this.ownsFolder = ownsFolder;
        Directory.CreateDirectory(TempFolder);
        TokenFile = Path.Combine(TempFolder, "PRWatch", "access-token");
        // The in-memory host's default Host is "localhost" with no port, which the Host check refuses.
        ClientOptions.BaseAddress = new Uri(LoopbackBase);
    }

    /// <summary>This factory's own temp folder.</summary>
    public string TempFolder { get; }

    /// <summary>The token file the hosted app uses (created by RoomKit on start).</summary>
    public string TokenFile { get; }

    /// <summary>The data folder: the token file's folder.</summary>
    public string DataFolder => Path.GetDirectoryName(TokenFile)!;

    /// <summary>The factory root passed as <c>--FactoryRoot</c> (set before the host starts); none when
    /// <c>null</c>.</summary>
    public string? FactoryRoot { get; set; }

    /// <summary>The app's clock.</summary>
    public FakeClock Clock { get; } = new(FakeClock.DefaultNow);

    /// <summary>Makes the data folder (<c>0700</c>) before the host starts, for files a test writes first.</summary>
    public string CreateDataFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(DataFolder);
        }
        else
        {
            Directory.CreateDirectory(
                DataFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return DataFolder;
    }

    /// <summary>The stored token; call after the host has started (the first client does that).</summary>
    public string ReadToken()
    {
        return File.ReadAllText(TokenFile).Trim();
    }

    /// <summary>A client that sends <c>Authorization: Bearer &lt;token&gt;</c> on every request.</summary>
    public HttpClient CreateTokenClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReadToken());
        return client;
    }

    /// <summary>WebApplicationFactory hands every host setting to <c>Program</c> as <c>--key=value</c>.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("TokenFile", TokenFile);
        if (FactoryRoot is not null)
        {
            builder.UseSetting("FactoryRoot", FactoryRoot);
        }

        builder.UseEnvironment(Environment);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && ownsFolder && Directory.Exists(TempFolder))
        {
            Directory.Delete(TempFolder, recursive: true);
        }
    }
}
