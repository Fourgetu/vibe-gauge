using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

internal sealed class ClaudeDesktopQuotaClient : IClaudeDesktopQuotaClient
{
    private static readonly HttpClient Official = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
    private readonly HttpClient http;
    private readonly Func<string, DateTimeOffset, ClaudeDesktopCredential?> credentials;
    private readonly TimeProvider clock;

    public ClaudeDesktopQuotaClient() : this(Official, ClaudeDesktopCredentials.Read) { }
    internal ClaudeDesktopQuotaClient(HttpClient http, Func<string, DateTimeOffset, ClaudeDesktopCredential?> credentials, TimeProvider? clock = null)
    { this.http = http; this.credentials = credentials; this.clock = clock ?? TimeProvider.System; }

    public ClaudeDesktopQuotaAccess ReadAccount(string profile)
    {
        try
        {
            var credential = credentials(profile, clock.GetUtcNow());
            return credential is null ? new(Error: "桌面额度凭据已过期或不可用，请打开 Claude 后刷新") : new(credential.Account);
        }
        catch { return new(Error: "无法读取桌面登录凭据，请打开 Claude 后刷新"); }
    }

    public async Task<ClaudeDesktopQuotaResult> ReadAsync(ClaudeDesktopQuotaAccount account, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            var credential = credentials(account.Profile, clock.GetUtcNow());
            if (!Matches(credential, account)) return new(Error: "Claude 登录信息已变化，等待重新查询");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            // Credentials are sent only to this official origin; production redirects are disabled.
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential!.Token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            if (!Matches(credentials(account.Profile, clock.GetUtcNow()), account)) return new(Error: "查询期间 Claude 登录信息发生变化");
            if (!response.IsSuccessStatusCode)
                return new(Error: response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Claude 额度授权已失效，请在桌面端重新登录后刷新",
                    HttpStatusCode.TooManyRequests => "Claude 额度查询暂被限流，稍后自动重试",
                    _ => "Claude 官方额度暂不可用，请稍后刷新"
                });
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
            return ClaudeDesktopQuotaParser.Parse(doc.RootElement, clock.GetUtcNow());
        }
        catch (OperationCanceledException) { return new(Error: "Claude 额度查询已取消或超时"); }
        catch { return new(Error: "Claude 官方额度查询失败，请检查登录和网络"); }
    }

    private static bool Matches(ClaudeDesktopCredential? credential, ClaudeDesktopQuotaAccount account) =>
        credential?.Account.Scope == account.Scope && credential.Account.Revision == account.Revision;
}
