using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class CodexProxyRsClient : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string? historyDirectory;
    private readonly SemaphoreSlim gate = new(1);
    private string scope = "", cookie = "";
    private DateTimeOffset expires;
    private RemoteUsageReport? lastGood;
    private IReadOnlyList<RemoteUsageBucket> retained = [];
    public CodexProxyRsClient(string? historyDirectory = null) : this(new HttpClient(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 2 * 1024 * 1024 }, historyDirectory)
    { ownsHttp = true; }
    public CodexProxyRsClient(HttpClient http, string? historyDirectory = null) { this.http = http; this.historyDirectory = historyDirectory; }

    public async Task<PlatformStatus> ProbeAsync(string address, string key, string title, CancellationToken token = default)
    {
        address = CodexProxyRsEndpoint.Normalize(address); UsageKeyVault.ValidateKey(key);
        try { await gate.WaitAsync(token); }
        catch (OperationCanceledException) { return CodexProxyRsParser.Failure(title, "查询已取消"); }
        PlatformStatus? budget = null;
        try
        {
            var nextScope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address + "\n" + key)));
            if (scope != nextScope)
            {
                scope = nextScope; cookie = ""; lastGood = null; retained = LoadHistory();
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(55));
            using (var data = await Get(address, "v1/usage", key, false, deadline.Token))
                budget = CodexProxyRsParser.Budget(title, data.RootElement, DateTimeOffset.Now);
            RemoteUsagePeriod today, recent;
            IReadOnlyList<RemoteUsageCall> calls;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (cookie.Length == 0 || expires <= DateTimeOffset.UtcNow.AddSeconds(30)) await Login(address, key, deadline.Token);
                    using (var data = await Get(address, "api/key-usage/overview?period=today", key, true, deadline.Token))
                        today = CodexProxyRsParser.Overview(data.RootElement);
                    using (var data = await Get(address, "api/key-usage/overview?period=30d", key, true, deadline.Token))
                        recent = CodexProxyRsParser.Overview(data.RootElement);
                    using (var data = await Get(address, "api/key-usage/records?period=30d&kind=all&currentPage=1&pageSize=20", key, true, deadline.Token))
                        calls = CodexProxyRsParser.Records(data.RootElement);
                    break;
                }
                catch (StatusException e) when (e.Status == HttpStatusCode.Unauthorized && attempt == 0)
                { cookie = ""; }
            }
            var merged = RemoteUsageHistory.Merge(retained, recent);
            // Validate aggregate bounds before saving data that the UI will sum.
            _ = RemoteUsageMetrics.Sum(merged.Select(x => x.Metrics));
            var note = "";
            // Missing/partial trend must not silently remove previously saved history.
            var measured = RemoteUsageMetrics.Sum(recent.Buckets.Select(x => x.Metrics));
            if (measured.Total == recent.Summary.Total && measured.Requests == recent.Summary.Requests)
            {
                retained = merged;
                if (!SaveHistory()) note = "历史缓存保存失败，本次统计仍可查看";
            }
            else note = "服务端趋势尚未完整，累计保留上次同步结果";
            lastGood = new(DateTimeOffset.Now, today, recent, retained, calls, note);
            return budget with { RemoteUsage = lastGood, ReportedTokens = new(today.Summary.Total, null),
                Detail = budget.Detail + "\n服务端用量见 API 页面；不与本地日志相加。" };
        }
        catch (Exception e) when (e is StatusException or HttpRequestException or OperationCanceledException or JsonException or
            InvalidDataException or OverflowException or CookieException)
        {
            var message = Error(e, token);
            if (e is StatusException { Status: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            { cookie = ""; lastGood = null; }
            var failed = budget ?? CodexProxyRsParser.Failure(title, message);
            return failed with { DataState = ProviderDataState.ReadFailed, CompactDetail = message,
                Detail = (budget is null ? "" : budget.Detail + "\n") + message,
                RemoteUsage = lastGood is null ? null : lastGood with { Note = "查询失败，以下为旧数据：" + message } };
        }
        finally { gate.Release(); }
    }

    private async Task Login(string address, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CodexProxyRsEndpoint.At(address, "api/auth/login"))
        { Content = JsonContent.Create(new { mode = "key", apiKey = key }) };
        // Explicit origin keeps loopback HTTP development deployments' cookie behavior compatible.
        request.Headers.TryAddWithoutValidation("Origin", request.RequestUri!.GetLeftPart(UriPartial.Authority));
        using var response = await http.SendAsync(request, token);
        Check(response);
        using var document = await Read(response, token);
        var data = CodexProxyRsParser.Data(document.RootElement);
        if (CodexProxyRsParser.Text(data, "role") != "key" ||
            !DateTimeOffset.TryParse(CodexProxyRsParser.Text(data, "expiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires))
            throw new JsonException("Invalid key session");
        var cookies = new CookieContainer();
        if (response.Headers.TryGetValues("Set-Cookie", out var headers))
            foreach (var header in headers) cookies.SetCookies(request.RequestUri!, header);
        var session = cookies.GetCookies(CodexProxyRsEndpoint.At(address, "api/key-usage/overview"))["cpr_session"];
        if (session is null || session.Expired || session.Value.Length == 0 || session.Value.Length > 4096) throw new JsonException("Missing session cookie");
        cookie = "cpr_session=" + session.Value;
    }
    private async Task<JsonDocument> Get(string address, string path, string key, bool session, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CodexProxyRsEndpoint.At(address, path));
        if (session) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        else request.Headers.Authorization = new("Bearer", key);
        using var response = await http.SendAsync(request, token);
        Check(response); return await Read(response, token);
    }
    private static async Task<JsonDocument> Read(HttpResponseMessage response, CancellationToken token)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Payload too large");
        return JsonDocument.Parse(bytes);
    }
    private sealed class StatusException(HttpStatusCode status) : Exception { public HttpStatusCode Status { get; } = status; }
    private static void Check(HttpResponseMessage response)
    { if (!response.IsSuccessStatusCode) throw new StatusException(response.StatusCode); }
    private static string Error(Exception error, CancellationToken token) => error switch
    {
        StatusException { Status: HttpStatusCode.Unauthorized } => "密钥无效或会话已失效（HTTP 401）",
        StatusException { Status: HttpStatusCode.Forbidden } => "密钥无权查询此站点（HTTP 403）",
        StatusException { Status: HttpStatusCode.NotFound } => "Codex Proxy RS 接口不存在，请检查地址和部署版本（HTTP 404）",
        StatusException { Status: HttpStatusCode.TooManyRequests } => "站点查询过于频繁，请稍后重试（HTTP 429）",
        StatusException e when (int)e.Status is >= 300 and < 400 => "站点要求跳转；未跟随跳转，请填写最终站点地址",
        StatusException e => $"站点查询失败（HTTP {(int)e.Status}）",
        OperationCanceledException => token.IsCancellationRequested ? "查询已取消" : "站点响应超时，请稍后重试",
        HttpRequestException => "连接失败，请检查地址、网络或 HTTPS 证书",
        _ => "返回格式不是支持的 Codex Proxy RS 用量数据"
    };
    private string HistoryFile => Path.Combine(historyDirectory!, scope + ".json");
    private IReadOnlyList<RemoteUsageBucket> LoadHistory()
    {
        if (historyDirectory is null) return [];
        try
        {
            if (!File.Exists(HistoryFile) || new FileInfo(HistoryFile).Length > 8 * 1024 * 1024) return [];
            var buckets = JsonSerializer.Deserialize<RemoteUsageBucket[]>(File.ReadAllText(HistoryFile)) ?? [];
            for (var i = 0; i < buckets.Length; i++)
            {
                var bucket = buckets[i];
                if (bucket is null || bucket.Metrics is not { } m || bucket.Label is null || bucket.Label.Length > 256 ||
                    bucket.Seconds is < 1 or > 172800 || bucket.Start > DateTimeOffset.MaxValue.AddSeconds(-bucket.Seconds) ||
                    i > 0 && buckets[i - 1].End > bucket.Start ||
                    m.Requests < 0 || m.Input < 0 || m.Output < 0 || m.Cached < 0 || m.CacheWrite < 0 ||
                    m.Reasoning < 0 || m.Total < 0 || m.CostUsd < 0) return [];
            }
            _ = RemoteUsageMetrics.Sum(buckets.Select(x => x.Metrics));
            return buckets;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OverflowException) { return []; }
    }
    private bool SaveHistory()
    {
        if (historyDirectory is null) return true;
        try
        {
            Directory.CreateDirectory(historyDirectory);
            File.WriteAllText(HistoryFile + ".tmp", JsonSerializer.Serialize(retained));
            File.Move(HistoryFile + ".tmp", HistoryFile, true); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose() { cookie = ""; if (ownsHttp) http.Dispose(); }
}
