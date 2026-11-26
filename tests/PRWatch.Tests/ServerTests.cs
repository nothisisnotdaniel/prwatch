using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PRWatch.Tests;

/// <summary>RoomKit's core as PRWatch runs it: <c>/health</c>, the token gate on <c>/api/version</c>, the Host
/// check and the security headers, all in memory.</summary>
public sealed class ServerTests
{
    private const string VersionRoute = "/api/version";

    [Fact]
    public async Task Health_ReturnsDevVersion_NoStore_AndSecurityHeaders()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"version\":\"dev\"}", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(RoomKitApp.ContentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task Version_WithoutToken_Is401()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(VersionRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(RoomKitApp.UnauthorizedBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Version_WithWrongToken_Is401()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        using HttpResponseMessage response = await client.GetAsync(VersionRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(RoomKitApp.UnauthorizedBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Version_WithTheTokenFromTheTokenFile_Is200()
    {
        using PrWatchFactory factory = new();
        using HttpClient anonymous = factory.CreateClient();
        using HttpResponseMessage first = await anonymous.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using HttpClient client = factory.CreateTokenClient();

        using HttpResponseMessage response = await client.GetAsync(VersionRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"version\":\"dev\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1:8100")]
    [InlineData("localhost:8100")]
    [InlineData("0.0.0.0:8101")]
    [InlineData("evil.example")]
    public async Task OtherHost_Is421BadHost(string host)
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await SendWithHost(client, host);

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"bad_host\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("127.0.0.1:8101")]
    [InlineData("localhost:8101")]
    public async Task OwnLoopbackHost_Passes(string host)
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await SendWithHost(client, host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Harness_UsesItsOwnTempTokenFile_AndProduction()
    {
        using PrWatchFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(
            ["--TokenFile=" + Path.Combine(factory.TempFolder, "PRWatch", "access-token"), "--environment=Production"],
            factory.Arguments);
        Assert.StartsWith(Path.GetTempPath(), factory.TokenFile, StringComparison.Ordinal);
        Assert.True(File.Exists(factory.TokenFile));
        Assert.NotEmpty(factory.ReadToken());
        Assert.Equal("Production", factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
    }

    [Fact]
    public void Tests_NeverPostTheUpdateSettings()
    {
        // The update settings route would reach GitHub; no test may call it.
        string route = "/api/update/" + "settings";
        string folder = RepoFiles.PathOf("tests/PRWatch.Tests");
        foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly))
        {
            Assert.DoesNotContain(route, File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    private static async Task<HttpResponseMessage> SendWithHost(HttpClient client, string host)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/health");
        request.Headers.Host = host;
        return await client.SendAsync(request);
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : "";
    }
}
