using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

/// <summary>
/// CodexBar-style Claude quota client supporting:
/// - "off" (default): disabled, zero network calls.
/// - "web": claude.ai Web API via user-pasted sessionKey cookie (no Claude Code required; stops cleanly on Cloudflare challenge).
/// - "oauth": api.anthropic.com OAuth usage via plain Claude Code ~/.claude/.credentials.json (no DPAPI decryption).
/// </summary>
internal sealed class ClaudeDesktopQuotaClient : IClaudeDesktopQuotaClient
{
    private static readonly HttpClient Official = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };

    private readonly HttpClient http;
    private readonly Func<string> readMode;
    private readonly Func<string, string, DateTimeOffset, ClaudeDesktopCredential?> readCredential;
    private readonly TimeProvider clock;
    private string? cachedOrgRevision;
    private string? cachedOrgId;

    public ClaudeDesktopQuotaClient(AppPaths? paths = null)
        : this(
            Official,
            () => FeaturePreferences.Load(paths ?? new AppPaths()).ClaudeQuotaSource,
            (mode, profile, now) => ClaudeDesktopCredentials.ReadForMode(paths ?? new AppPaths(), mode, profile, now))
    { }

    internal ClaudeDesktopQuotaClient(
        HttpClient http,
        Func<string> readMode,
        Func<string, string, DateTimeOffset, ClaudeDesktopCredential?> readCredential,
        TimeProvider? clock = null)
    {
        this.http = http;
        this.readMode = readMode;
        this.readCredential = readCredential;
        this.clock = clock ?? TimeProvider.System;
    }

    public ClaudeDesktopQuotaAccess ReadAccount(string profile)
    {
        var mode = NormalizeMode(readMode());
        if (mode == "off")
            return new(Disabled: true, Error: "默认仅统计本地用量；可在「系统 → Claude 额度来源」启用手动 sessionKey 或 Claude Code OAuth");
        try
        {
            var credential = readCredential(mode, profile, clock.GetUtcNow());
            if (credential is null)
            {
                return mode == "web"
                    ? new(Error: "尚未保存 claude.ai sessionKey；请在「系统 → Claude 额度来源」粘贴并保存")
                    : new(Error: "未检测到含 user:profile 权限的 ~/.claude/.credentials.json，请先登录 Claude Code");
            }
            return new(credential.Account);
        }
        catch
        {
            return new(Error: "无法读取 Claude 额度凭据，请在「系统 → Claude 额度来源」检查配置");
        }
    }

    public async Task<ClaudeDesktopQuotaResult> ReadAsync(ClaudeDesktopQuotaAccount account, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            var mode = NormalizeMode(readMode());
            if (mode != account.SourceKind)
                return new(Error: "Claude 额度来源设置已变更，等待重新查询");

            var credential = readCredential(mode, account.Profile, clock.GetUtcNow());
            if (!Matches(credential, account))
                return new(Error: "Claude 登录凭据已变化，等待重新查询");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var result = mode == "web"
                ? await ReadWebAsync(credential!, timeout.Token).ConfigureAwait(false)
                : await ReadOAuthAsync(credential!, timeout.Token).ConfigureAwait(false);

            if (!Matches(readCredential(mode, account.Profile, clock.GetUtcNow()), account))
                return new(Error: "查询期间 Claude 登录凭据发生变化");

            return result;
        }
        catch (OperationCanceledException) { return new(Error: "Claude 额度查询已取消或超时"); }
        catch { return new(Error: "Claude 额度查询失败，请检查凭据和网络"); }
    }

    public static async Task<ClaudeDesktopQuotaResult> TestWebSessionKeyAsync(string rawInput, CancellationToken cancellationToken)
    {
        var key = ClaudeDesktopCredentials.ExtractSessionKey(rawInput);
        if (key is null)
            return new(Error: "无效的 sessionKey：请粘贴 claude.ai 的 sessionKey（以 sk-ant- 开头）或完整 Cookie 请求头");
        var tempAccount = new ClaudeDesktopQuotaAccount("test", "test", "test", "Web 订阅", "web");
        var tempClient = new ClaudeDesktopQuotaClient(Official, () => "web", (_, _, _) => new(tempAccount, key));
        return await tempClient.ReadAsync(tempAccount, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ClaudeDesktopQuotaResult> ReadOAuthAsync(ClaudeDesktopCredential credential, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return MapHttpError(response, isWeb: false);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
        return ClaudeDesktopQuotaParser.Parse(doc.RootElement, clock.GetUtcNow());
    }

    private async Task<ClaudeDesktopQuotaResult> ReadWebAsync(ClaudeDesktopCredential credential, CancellationToken token)
    {
        var orgId = cachedOrgRevision == credential.Account.Revision ? cachedOrgId : null;
        if (string.IsNullOrEmpty(orgId))
        {
            using var orgReq = CreateWebRequest("https://claude.ai/api/organizations", credential.Secret);
            using var orgRes = await http.SendAsync(orgReq, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            if (!orgRes.IsSuccessStatusCode)
                return MapHttpError(orgRes, isWeb: true);
            using var orgDoc = JsonDocument.Parse(await orgRes.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
            orgId = ExtractOrgUuid(orgDoc.RootElement);
            if (string.IsNullOrEmpty(orgId))
                return new(Error: "未在 claude.ai 返回中找到有效的组织 ID，请检查 sessionKey");
            cachedOrgRevision = credential.Account.Revision;
            cachedOrgId = orgId;
        }

        using var usageReq = CreateWebRequest($"https://claude.ai/api/organizations/{Uri.EscapeDataString(orgId)}/usage", credential.Secret);
        using var usageRes = await http.SendAsync(usageReq, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
        if (!usageRes.IsSuccessStatusCode)
        {
            if (usageRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { cachedOrgRevision = null; cachedOrgId = null; }
            return MapHttpError(usageRes, isWeb: true);
        }
        using var doc = JsonDocument.Parse(await usageRes.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
        return ClaudeDesktopQuotaParser.Parse(doc.RootElement, clock.GetUtcNow());
    }

    private static HttpRequestMessage CreateWebRequest(string url, string sessionKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", "sessionKey=" + sessionKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        return req;
    }

    private static string? ExtractOrgUuid(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String)
            {
                var id = uuid.GetString()?.Trim();
                if (!string.IsNullOrEmpty(id) && id.Length <= 80 && id.All(c => char.IsLetterOrDigit(c) || c == '-'))
                    return id;
            }
        }
        return null;
    }

    private static ClaudeDesktopQuotaResult MapHttpError(HttpResponseMessage response, bool isWeb)
    {
        if (isWeb && IsCloudflareChallenge(response))
            return new(Error: "claude.ai 触发了 Cloudflare 验证（不会尝试绕过），请更换网络、使用 OAuth 模式或直接打开官方用量页", RateLimited: true);

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(Error: isWeb
                ? "claude.ai sessionKey 已失效或被拒绝，请重新复制并保存"
                : "Claude OAuth 授权已失效，请重新登录 Claude Code 后刷新"),
            HttpStatusCode.TooManyRequests => new(Error: "Claude 额度查询暂被限流（429），已自动延长重试间隔", RateLimited: true),
            _ => new(Error: $"Claude 额度接口暂不可用（HTTP {(int)response.StatusCode}），稍后自动重试")
        };
    }

    private static bool IsCloudflareChallenge(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("cf-mitigated", out var vals) &&
            vals.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
            return true;
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        return response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
            && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "web" => "web",
        "oauth" => "oauth",
        _ => "off"
    };

    private static bool Matches(ClaudeDesktopCredential? credential, ClaudeDesktopQuotaAccount account) =>
        credential?.Account.Scope == account.Scope && credential.Account.Revision == account.Revision &&
        credential.Account.SourceKind == account.SourceKind;
}
