using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class ClashControllerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-clash-test-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));
    private const string Address = "http://127.0.0.1:9090";

    [Fact]
    public void SecretIsEncryptedBoundToEndpointAndReadImmediatelyAfterSaveReplaceOrRemove()
    {
        var store = new ClashControllerCredentials(Paths, () => "environment-fixture");
        Assert.Equal("environment-fixture", store.Resolve(Address));
        store.Save(Address, "saved-fixture-one");
        Assert.DoesNotContain("saved-fixture-one", File.ReadAllText(store.FilePath));
        Assert.Equal("saved-fixture-one", new ClashControllerCredentials(Paths).ReadSaved(Address));
        Assert.Equal("saved-fixture-one", store.Resolve("http://localhost:9090/"));
        Assert.Null(store.ReadSaved("http://127.0.0.1:9091"));
        Assert.Equal("environment-fixture", store.Resolve("http://127.0.0.1:9091"));
        store.Save(Address, "saved-fixture-two");
        Assert.Equal("saved-fixture-two", store.Resolve(Address));
        store.Remove(Address);
        Assert.False(File.Exists(store.FilePath));
        Assert.Equal("environment-fixture", store.Resolve(Address));
        Assert.False(File.Exists(Path.Combine(Paths.LocalDataRoot, "features.json")));
    }
    [Fact]
    public void DamagedSecretFailsExplicitlyWithoutReturningCiphertextOrUsingFallback()
    {
        var store = new ClashControllerCredentials(Paths, () => "environment-fixture");
        store.Save(Address, "private-fixture");
        File.WriteAllText(store.FilePath, "{broken-private-fixture");
        Assert.Equal(ClashControllerCredentials.UnreadableSecret, Assert.Throws<InvalidOperationException>(() => store.Resolve(Address)).Message);
        store.Save(Address, "replacement-fixture");
        Assert.Equal("replacement-fixture", store.Resolve(Address));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("https://example.com")]
    [InlineData("http://192.168.1.1:9090")]
    [InlineData("http://user:password@127.0.0.1:9090")]
    [InlineData("http://127.0.0.1:9090/path")]
    [InlineData("http://127.0.0.1:9090?secret=fixture")]
    [InlineData("http://127.0.0.1:9090#fragment")]
    public async Task UnsafeEndpointsAreRejectedBeforeReadingOrSendingAnyKey(string? address)
    {
        // A null preference can occur in hand-edited JSON; it must fail without sending.
        if (address is null) { Directory.CreateDirectory(Paths.LocalDataRoot); File.WriteAllText(Path.Combine(Paths.LocalDataRoot, "features.json"), "{\"ClashController\":null}"); }
        var sent = 0;
        var handler = new ReplyHandler(_ => { sent++; throw new Exception("must not send"); });
        var client = new ClashControllerClient(Paths, handler, () => throw new Exception("must not read"));
        var result = await client.ReadAsync(address);
        Assert.False(result.Available); Assert.Equal(0, sent);
        Assert.Equal(ClashControllerCredentials.InvalidEndpoint, result.Error);
    }
    [Theory]
    [InlineData("http://localhost:9090", "http://127.0.0.1:9090/")]
    [InlineData("http://[::1]:9090", "http://[::1]:9090/")]
    public void LocalControllerAddressesAreCanonicalized(string source, string expected) =>
        Assert.Equal(expected, ClashControllerCredentials.NormalizeEndpoint(source).AbsoluteUri);
    [Theory]
    [InlineData(401, "认证失败")]
    [InlineData(403, "认证失败")]
    [InlineData(404, "接口不存在")]
    [InlineData(500, "HTTP 500")]
    [InlineData(302, "重定向")]
    public async Task HttpFailuresReachGeminiWithoutLeakingResponseBodiesOrSecrets(int status, string message)
    {
        var handler = new ReplyHandler(_ => new((HttpStatusCode)status) { Content = new StringContent("private-response-fixture") });
        var result = await new ClashControllerClient(Paths, handler, () => null).ReadAsync(Address, "private-key-fixture");
        Assert.False(result.Available); Assert.Contains(message, result.Error);
        Assert.Equal(result.Error, ClashControllerClient.GeminiExit(result).Error);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(result));
    }
    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"connections\":null}")]
    public async Task InvalidConnectionResponsesAreNotTreatedAsSuccessfulEmptyTables(string response)
    {
        var result = await new ClashControllerClient(Paths, new ReplyHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(response) }), () => null).ReadAsync(Address);
        Assert.False(result.Available); Assert.Contains("格式无效", result.Error);
    }
    [Fact]
    public async Task TestInputOverridesSavedAndEnvironmentWithoutSavingAndOnlyConnectionsMustSucceed()
    {
        var store = new ClashControllerCredentials(Paths); store.Save(Address, "saved-key");
        var handler = new ReplyHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("typed-key", request.Headers.Authorization.Parameter);
            return request.RequestUri!.AbsolutePath == "/proxies" ? new(HttpStatusCode.NotFound) :
                new(HttpStatusCode.OK) { Content = new StringContent("{\"connections\":[]}") };
        });
        var result = await new ClashControllerClient(Paths, handler, () => "env-key").ReadAsync(Address, "typed-key");
        Assert.True(result.Available); Assert.Empty(result.Error);
        Assert.Equal("未发现 Gemini 活动连接", ClashControllerClient.GeminiExit(result).Error);
        Assert.Equal("saved-key", store.ReadSaved(Address));
    }
    [Fact]
    public async Task RealLoopbackAuthenticationWorksAfterSavingWithoutRestartAndFailsAfterReplacingWithWrongKey()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 6; i++)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var first = await reader.ReadLineAsync(timeout.Token);
                var authorized = false;
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                    if (line == "Authorization: Bearer correct-fixture-key") authorized = true;
                var body = !authorized ? "{}" : first!.Contains("/connections ") ?
                    "{\"connections\":[{\"metadata\":{\"host\":\"gemini.google.com\"},\"chains\":[\"Gemini route\",\"Test node\"]}]}" : "{\"proxies\":{}}";
                var bytes = Encoding.UTF8.GetBytes(body);
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(authorized ? "200 OK" : "401 Unauthorized")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, timeout.Token); await stream.WriteAsync(bytes, timeout.Token);
            }
        }, timeout.Token);
        try
        {
            (new FeaturePreferences { ClashController = address }).Save(Paths);
            var client = new ClashControllerClient(Paths, environmentSecret: () => null);
            Assert.Contains("认证失败", (await client.ReadAsync()).Error);
            var store = new ClashControllerCredentials(Paths); store.Save(address, "correct-fixture-key");
            var success = await client.ReadAsync();
            Assert.True(success.Available);
            var gemini = ClashControllerClient.GeminiExit(success);
            Assert.Empty(gemini.Error); Assert.Contains("Gemini route → Test node", gemini.Chain); Assert.Empty(gemini.Ip);
            store.Save(address, "wrong-fixture-key");
            Assert.Contains("认证失败", (await client.ReadAsync()).Error);
            await server;
        }
        finally { timeout.Cancel(); listener.Stop(); }
    }
    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
    [Theory]
    [InlineData("http://192.168.10.1:9090")]
    [InlineData("http://10.0.0.1:9090")]
    [InlineData("https://172.16.0.1:9090")]
    [InlineData("http://[fd00::1]:9090")]
    public void PrivateRouterRequiresExplicitLanMode(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => ClashControllerCredentials.NormalizeEndpoint(endpoint));
        Assert.Equal(endpoint + "/", ClashControllerCredentials.NormalizeEndpoint(endpoint, true).AbsoluteUri);
    }
    [Theory]
    [InlineData("http://8.8.8.8:9090")]
    [InlineData("http://example.com:9090")]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://172.32.0.1:9090")]
    [InlineData("http://[2001:db8::1]:9090")]
    public void LanModeDoesNotPermitPublicOrArbitraryHostAddresses(string endpoint) =>
        Assert.Throws<ArgumentException>(() => ClashControllerCredentials.NormalizeEndpoint(endpoint, true));
    [Fact]
    public async Task RouterUsesOnlyItsOwnSecretAndFiltersConnectionsBeforeDisplayingThem()
    {
        const string router = "http://192.168.10.1:9090";
        var store = new ClashControllerCredentials(Paths, () => "local-env-private", allowLan: true);
        store.Save(Address, "local-private");
        Assert.Null(store.Resolve(router));
        store.Save(router, "router-private");
        Assert.Equal("local-private", store.Resolve(Address));
        Assert.Equal("router-private", store.Resolve(router));
        var body = "{\"connections\":[{\"metadata\":{\"host\":\"gemini.google.com\",\"sourceIP\":\"192.168.10.8\"},\"chains\":[\"this-pc\"]},{\"metadata\":{\"host\":\"gemini.google.com\",\"sourceIP\":\"192.168.10.9\"},\"chains\":[\"other-device\"]}]}";
        var handler = new ReplyHandler(request =>
        {
            Assert.Equal("192.168.10.1", request.RequestUri!.Host);
            Assert.Equal("router-private", request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath == "/proxies" ? "{\"proxies\":{}}" : body) };
        });
        new FeaturePreferences { ClashController = router, LanClashController = true, ClashSourceIp = "192.168.10.8" }.Save(Paths);
        var result = await new ClashControllerClient(Paths, handler, () => "local-env-private").ReadAsync();
        Assert.True(result.Available); Assert.Single(result.Connections);
        Assert.Contains("192.168.10.8", result.Scope);
        Assert.DoesNotContain("other-device", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
        var gemini = ClashControllerClient.GeminiExit(result);
        Assert.Contains(result.Scope, gemini.Host); Assert.Contains("this-pc", gemini.Chain);
        store.Remove(router);
        Assert.Null(store.Resolve(router)); Assert.Equal("local-private", store.Resolve(Address));
    }
    [Fact]
    public async Task RouterAllDevicesIsLabeledAndNeverReceivesEnvironmentSecret()
    {
        var handler = new ReplyHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath == "/proxies" ? "{\"proxies\":{}}" : "{\"connections\":[]}") };
        });
        var result = await new ClashControllerClient(Paths, handler, () => "must-not-be-sent").ReadAsync("http://192.168.10.1:9090", allowLan: true);
        Assert.True(result.Available); Assert.Equal("软路由全设备连接表", result.Scope);
        Assert.Contains(result.Scope, ClashControllerClient.GeminiExit(result).Host);
    }
    [Fact]
    public void PreviousSingleControllerCredentialMigratesWithoutLosingItsKey()
    {
        var store = new ClashControllerCredentials(Paths, allowLan: true); store.Save(Address, "local-key");
        using var doc = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        var ciphertext = doc.RootElement.GetProperty("Secrets").GetProperty(Address + "/").GetString();
        File.WriteAllText(store.FilePath, JsonSerializer.Serialize(new { Endpoint = Address + "/", Ciphertext = ciphertext }));
        Assert.Equal("local-key", store.ReadSaved(Address));
        store.Save("http://192.168.10.1:9090", "router-key");
        Assert.Equal("local-key", store.ReadSaved(Address));
        Assert.Equal("router-key", store.ReadSaved("http://192.168.10.1:9090"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
