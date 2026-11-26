using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PRWatch.Tests;

/// <summary>Hosts PRWatch in memory (no real port) with its own temp folder, deleted on dispose. The host gets
/// <c>--TokenFile=&lt;temp&gt;/PRWatch/access-token</c> and <c>--environment=Production</c>, so RoomKit's data
/// folder (token, push and update settings) is the temp one, never <c>~/Library/Application Support/PRWatch</c>.
/// Clients send <c>Host: 127.0.0.1:8101</c>.</summary>
internal sealed class PrWatchFactory : WebApplicationFactory<Program>
{
    public const string Environment = "Production";

    public const string LoopbackBase = "http://127.0.0.1:8101/";

    public PrWatchFactory()
    {
        TempFolder = Path.Combine(Path.GetTempPath(), "prwatch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempFolder);
        TokenFile = Path.Combine(TempFolder, "PRWatch", "access-token");
        // The in-memory host's default Host is "localhost" with no port, which the Host check refuses.
        ClientOptions.BaseAddress = new Uri(LoopbackBase);
    }

    /// <summary>This factory's own temp folder.</summary>
    public string TempFolder { get; }

    /// <summary>The token file the hosted app uses (created by RoomKit on start).</summary>
    public string TokenFile { get; }

    /// <summary>The host arguments: WebApplicationFactory hands every host setting to <c>Program</c> as
    /// <c>--key=value</c>.</summary>
    public IReadOnlyList<string> Arguments => ["--TokenFile=" + TokenFile, "--environment=" + Environment];

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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("TokenFile", TokenFile);
        builder.UseEnvironment(Environment);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(TempFolder))
        {
            Directory.Delete(TempFolder, recursive: true);
        }
    }
}
