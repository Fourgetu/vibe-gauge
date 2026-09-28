using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VibeGauge.Proxy.Security;
using VibeGauge.Proxy.Usage;
using Xunit;

namespace VibeGauge.Proxy.Tests;

public sealed class ProxyIntegrationTests
{
    [Fact]
    public async Task HealthIdentifiesProxyAndShutdownRequiresControlToken()
    {
        var port = ProxyFixture.FreePort();
        var directory = ProxyFixture.TempDirectory();
        const string controlToken = "fixture-control-token";
        await using var host = new ProxyHost(new ProxyOptions
        {
            Port = port,
            DataDirectory = directory,
            ControlToken = controlToken
        });
        await host.StartAsync();
        using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}") };

        using var health = await client.GetAsync("/_vibegauge/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var healthDocument = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        Assert.Equal("VibeGauge.Proxy", healthDocument.RootElement.GetProperty("product").GetString());

        using var denied = new HttpRequestMessage(HttpMethod.Post, "/_vibegauge/shutdown");
        denied.Headers.TryAddWithoutValidation("X-VibeGauge-Control", "wrong");
        using var deniedResponse = await client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);

        using var shutdown = new HttpRequestMessage(HttpMethod.Post, "/_vibegauge/shutdown");
        shutdown.Headers.TryAddWithoutValidation("X-VibeGauge-Control", controlToken);
        using var shutdownResponse = await client.SendAsync(shutdown);
        Assert.Equal(HttpStatusCode.Accepted, shutdownResponse.StatusCode);
    }

    [Fact]
    public void CompatibilityFixturesMatchPythonBehavior()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "ProxyParity"));
        AssertFixture(Path.Combine(root, "openai-response.json"), Path.Combine(root, "openai-expected.json"));
        AssertFixture(Path.Combine(root, "anthropic-response.json"), Path.Combine(root, "anthropic-expected.json"));
    }

    [Fact]
    public async Task OpenAiJsonForwardsAndNeverPersistsSecrets()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        const string bearer = "SUPER_SECRET_KEY";
        const string apiKey = "SUPER_SECRET_2";
        const string queryKey = "SUPER_SECRET_3";
        using var request = fixture.Post("/openai-json?key=" + queryKey, """{"model":"deepseek-chat","messages":[{"role":"user","content":"你好"}]}""");
        request.Headers.Authorization = new("Bearer", bearer);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        using var response = await fixture.Client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("你好", responseText);

        var record = await fixture.WaitForRecordAsync(1);
        Assert.Equal(100, record.GetProperty("ctx").GetInt64());
        Assert.Equal(60, record.GetProperty("cache_read").GetInt64());
        Assert.Equal(20, record.GetProperty("out").GetInt64());
        Assert.Equal(4, record.GetProperty("think").GetInt64());
        Assert.Equal("/openai-json", record.GetProperty("path").GetString());
        var allFiles = string.Join("\n", Directory.EnumerateFiles(fixture.DataDirectory).Select(File.ReadAllText));
        Assert.DoesNotContain(bearer, allFiles);
        Assert.DoesNotContain(apiKey, allFiles);
        Assert.DoesNotContain(queryKey, allFiles);
        using var health = await fixture.Client.GetAsync("/_vibegauge/health");
        var healthText = await health.Content.ReadAsStringAsync();
        Assert.DoesNotContain(bearer, healthText);
        Assert.DoesNotContain(apiKey, healthText);
        Assert.DoesNotContain(queryKey, healthText);
        var captured = fixture.Upstream.Requests.Last();
        Assert.Equal("Bearer " + bearer, captured.Authorization);
        Assert.Equal(apiKey, captured.ApiKey);
        Assert.Contains("key=" + queryKey, captured.PathAndQuery);
    }

    [Fact]
    public async Task OpenAiResponsesJsonIsParsed()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var response = await fixture.Client.SendAsync(fixture.Post("/openai-responses", """{"model":"gpt-test","input":"hello"}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = await fixture.WaitForRecordAsync(1);
        Assert.Equal(44, record.GetProperty("ctx").GetInt64());
        Assert.Equal(11, record.GetProperty("cache_read").GetInt64());
        Assert.Equal(9, record.GetProperty("out").GetInt64());
        Assert.Equal(3, record.GetProperty("think").GetInt64());
    }

    [Fact]
    public async Task OpenAiSseStreamsAndParsesFinalUsage()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        var started = DateTimeOffset.UtcNow;
        using var response = await fixture.Client.SendAsync(
            fixture.Post("/openai-sse", """{"model":"openrouter/test","stream":true}"""),
            HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer);
        Assert.True(read > 0);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(1));
        var remainder = await new StreamReader(stream).ReadToEndAsync();
        Assert.Contains("[DONE]", Encoding.UTF8.GetString(buffer, 0, read) + remainder);
        var record = await fixture.WaitForRecordAsync(1);
        Assert.True(record.GetProperty("stream").GetBoolean());
        Assert.Equal(80, record.GetProperty("ctx").GetInt64());
        Assert.Equal(50, record.GetProperty("cache_read").GetInt64());
        Assert.Equal(12, record.GetProperty("out").GetInt64());
    }

    [Fact]
    public async Task AnthropicJsonIsParsed()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var response = await fixture.Client.SendAsync(fixture.Post("/anthropic-json", """{"model":"glm-4.7","messages":[]}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = await fixture.WaitForRecordAsync(1);
        Assert.Equal(17, record.GetProperty("ctx").GetInt64());
        Assert.Equal(5, record.GetProperty("cache_read").GetInt64());
        Assert.Equal(2, record.GetProperty("cache_write").GetInt64());
        Assert.Equal(7, record.GetProperty("out").GetInt64());
    }

    [Fact]
    public async Task AnthropicSseCombinesUsageOnce()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var response = await fixture.Client.SendAsync(fixture.Post("/anthropic-sse", """{"model":"claude-test","stream":true,"messages":[]}"""));
        Assert.Contains("message_stop", await response.Content.ReadAsStringAsync());
        var record = await fixture.WaitForRecordAsync(1);
        Assert.Equal(21, record.GetProperty("ctx").GetInt64());
        Assert.Equal(6, record.GetProperty("cache_read").GetInt64());
        Assert.Equal(3, record.GetProperty("cache_write").GetInt64());
        Assert.Equal(8, record.GetProperty("out").GetInt64());
        Assert.Single(File.ReadAllLines(fixture.LogPath));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task UpstreamErrorStatusIsPreserved(int status)
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var response = await fixture.Client.SendAsync(fixture.Post("/status/" + status, "{}"));
        Assert.Equal(status, (int)response.StatusCode);
        var record = await fixture.WaitForRecordAsync(1);
        Assert.Equal(status, record.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task TimeoutReturns504AndProxyStaysHealthy()
    {
        await using var fixture = await ProxyFixture.StartAsync(TimeSpan.FromMilliseconds(250));
        using var response = await fixture.Client.SendAsync(fixture.Post("/timeout", "{}"));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        using var health = await fixture.Client.GetAsync("/_vibegauge/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task InvalidJsonAndMissingUsageAreNotFabricated()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var invalid = await fixture.Client.SendAsync(fixture.Post("/invalid", """{"model":"fixture"}"""));
        using var empty = await fixture.Client.SendAsync(fixture.Post("/no-usage", "{}"));
        _ = await invalid.Content.ReadAsStringAsync();
        _ = await empty.Content.ReadAsStringAsync();
        var lines = await fixture.WaitForLinesAsync(2);
        Assert.All(lines, line =>
        {
            using var document = JsonDocument.Parse(line);
            Assert.False(document.RootElement.GetProperty("parsed").GetBoolean());
            Assert.Equal(0, document.RootElement.GetProperty("ctx").GetInt64());
        });
    }

    [Fact]
    public async Task UnknownModelUnicodeAndLargeRequestAreHandled()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        var unicode = """{"messages":[{"role":"user","content":"你好，世界"}]}""";
        using var first = await fixture.Client.SendAsync(fixture.Post("/echo", unicode));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var large = JsonSerializer.Serialize(new { model = "large", padding = new string('x', 1024 * 1024) });
        using var second = await fixture.Client.SendAsync(fixture.Post("/large", large));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var lines = await fixture.WaitForLinesAsync(2);
        using var firstRecord = JsonDocument.Parse(lines[0]);
        Assert.Equal("fixture-model", firstRecord.RootElement.GetProperty("model").GetString());
        Assert.Contains("你好", fixture.Upstream.Requests.First().Body);
        Assert.Equal("example.com", ProviderResolver.ForHost("api.example.com"));
    }

    [Fact]
    public async Task DefaultSsrfPolicyBlocksLoopbackAndInvalidSchemes()
    {
        var proxyPort = ProxyFixture.FreePort();
        var directory = ProxyFixture.TempDirectory();
        await using var host = new ProxyHost(new ProxyOptions { Port = proxyPort, DataDirectory = directory });
        await host.StartAsync();
        using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{proxyPort}") };
        using var blocked = await client.PostAsync("/http://127.0.0.1:1/private", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        using var invalid = await client.PostAsync("/file:///windows/system.ini", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public void SsrfPolicyBlocksIpv4MappedPrivateAddresses()
    {
        var validator = new HostValidator(allowLoopback: false);
        Assert.False(validator.IsAllowed(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(validator.IsAllowed(IPAddress.Parse("::ffff:192.168.1.10")));
        Assert.True(validator.IsAllowed(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Fact]
    public async Task PortOccupationAndDuplicateStartFailWithoutKillingOwner()
    {
        var port = ProxyFixture.FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        await using (var blocked = new ProxyHost(new ProxyOptions { Port = port, DataDirectory = ProxyFixture.TempDirectory() }))
            await Assert.ThrowsAnyAsync<Exception>(() => blocked.StartAsync());
        blocker.Stop();

        await using var first = new ProxyHost(new ProxyOptions { Port = port, DataDirectory = ProxyFixture.TempDirectory(), AllowLoopbackUpstream = true });
        await first.StartAsync();
        await using var second = new ProxyHost(new ProxyOptions { Port = port, DataDirectory = ProxyFixture.TempDirectory(), AllowLoopbackUpstream = true });
        await Assert.ThrowsAnyAsync<Exception>(() => second.StartAsync());
    }

    [Fact]
    public async Task ClientDisconnectAndBrokenStreamDoNotCrashProxy()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort);
            var request = Encoding.ASCII.GetBytes(
                $"POST /http://127.0.0.1:{fixture.Upstream.Port}/echo HTTP/1.1\r\nHost: 127.0.0.1:{fixture.ProxyPort}\r\nContent-Length: 1000000\r\n\r\n{{");
            await tcp.GetStream().WriteAsync(request);
        }
        using var broken = await fixture.Client.SendAsync(
            fixture.Post("/stream-break", """{"model":"broken","stream":true}"""),
            HttpCompletionOption.ResponseHeadersRead);
        try { _ = await broken.Content.ReadAsByteArrayAsync(); } catch { }
        using var health = await fixture.Client.GetAsync("/_vibegauge/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRequestsProduceCompleteJsonLines()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        var calls = Enumerable.Range(0, 24).Select(async index =>
        {
            using var response = await fixture.Client.SendAsync(fixture.Post("/openai-json", $$"""{"model":"m{{index}}"}"""));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
        await Task.WhenAll(calls);
        var lines = await fixture.WaitForLinesAsync(24);
        Assert.Equal(24, lines.Length);
        foreach (var line in lines) using (JsonDocument.Parse(line)) { }
    }

    private static void AssertFixture(string responsePath, string expectedPath)
    {
        using var expectedDocument = JsonDocument.Parse(File.ReadAllText(expectedPath));
        var expected = expectedDocument.RootElement;
        var usage = new UsageAccumulator(null);
        UsageParser.Apply(File.ReadAllBytes(responsePath), usage);
        Assert.Equal(expected.GetProperty("model").GetString(), usage.Model);
        Assert.Equal(expected.GetProperty("ctx").GetInt64(), usage.ContextTokens);
        Assert.Equal(expected.GetProperty("cache_read").GetInt64(), usage.CacheReadTokens);
        Assert.Equal(expected.GetProperty("cache_write").GetInt64(), usage.CacheWriteTokens);
        Assert.Equal(expected.GetProperty("out").GetInt64(), usage.OutputTokens);
        Assert.Equal(expected.GetProperty("think").GetInt64(), usage.ThinkingTokens);
    }
}

internal sealed class ProxyFixture : IAsyncDisposable
{
    private readonly ProxyHost proxy;

    private ProxyFixture(FakeUpstreamServer upstream, ProxyHost proxy, HttpClient client, string directory, int proxyPort)
    {
        Upstream = upstream;
        this.proxy = proxy;
        Client = client;
        DataDirectory = directory;
        ProxyPort = proxyPort;
    }

    public FakeUpstreamServer Upstream { get; }
    public HttpClient Client { get; }
    public string DataDirectory { get; }
    public int ProxyPort { get; }
    public string LogPath => Path.Combine(DataDirectory, "api-calls.jsonl");

    public static async Task<ProxyFixture> StartAsync(TimeSpan? timeout = null)
    {
        var upstream = new FakeUpstreamServer();
        var proxyPort = FreePort();
        var directory = TempDirectory();
        var proxy = new ProxyHost(new ProxyOptions
        {
            Port = proxyPort,
            DataDirectory = directory,
            AllowLoopbackUpstream = true,
            UpstreamTimeout = timeout ?? TimeSpan.FromSeconds(5)
        });
        await proxy.StartAsync();
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{proxyPort}"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        return new(upstream, proxy, client, directory, proxyPort);
    }

    public HttpRequestMessage Post(string path, string body)
    {
        var upstream = $"http://127.0.0.1:{Upstream.Port}{path}";
        return new(HttpMethod.Post, "/" + upstream)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    public async Task<JsonElement> WaitForRecordAsync(int count)
    {
        var lines = await WaitForLinesAsync(count);
        using var document = JsonDocument.Parse(lines[^1]);
        return document.RootElement.Clone();
    }

    public async Task<string[]> WaitForLinesAsync(int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(LogPath))
            {
                var lines = ReadSharedLines(LogPath);
                if (lines.Length >= count) return lines;
            }
            await Task.Delay(30);
        }
        return File.Exists(LogPath) ? ReadSharedLines(LogPath) : [];
    }

    private static string[] ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return [.. lines];
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "vibegauge-proxy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await proxy.DisposeAsync();
        await Upstream.DisposeAsync();
        try { Directory.Delete(DataDirectory, true); } catch { }
    }
}
