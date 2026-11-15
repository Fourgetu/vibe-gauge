namespace VibeGauge.Core;

// Only opaque account/credential fingerprints cross this boundary, never bearer tokens.
public sealed record ClaudeDesktopQuotaAccount(string Profile, string Scope, string Revision, string Tier);
public sealed record ClaudeDesktopQuotaAccess(ClaudeDesktopQuotaAccount? Account = null, string Error = "");
public sealed record ClaudeDesktopQuotaResult(QuotaWindow? FiveHour = null, QuotaWindow? Weekly = null,
    IReadOnlyList<NamedQuota>? Extra = null, string Error = "");

public interface IClaudeDesktopQuotaClient
{
    ClaudeDesktopQuotaAccess ReadAccount(string profile);
    Task<ClaudeDesktopQuotaResult> ReadAsync(ClaudeDesktopQuotaAccount account, CancellationToken cancellationToken);
}

public sealed class ClaudeDesktopQuotaRefresh(IClaudeDesktopQuotaClient client, TimeProvider? time = null) : IDisposable
{
    private readonly object sync = new();
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private ClaudeDesktopQuotaAccount? current;
    private ClaudeDesktopQuotaResult? cached;
    private Task<ClaudeDesktopQuotaResult>? pending;
    private CancellationTokenSource? cancellation;
    private DateTimeOffset attempted = DateTimeOffset.MinValue;
    private DateTimeOffset succeeded = DateTimeOffset.MinValue;
    private string error = "";
    private bool force, disposed;

    public PlatformStatus? Apply(PlatformStatus? local)
    {
        lock (sync)
        {
            if (disposed) return local;
            if (local is null || local.Tier != "已登录" || local.Metadata?.Sources.Count != 1)
            { Reset(); return local; }
            ClaudeDesktopQuotaAccess access;
            try { access = client.ReadAccount(Path.GetDirectoryName(local.Metadata.Sources[0])!); }
            catch { access = new(Error: "无法读取桌面登录凭据，请打开 Claude 后刷新"); }
            if (access.Account is not { } account)
            {
                Reset();
                var message = access.Error.Length > 0 ? access.Error : "桌面额度凭据暂不可用，请打开 Claude 后刷新";
                return local with { Detail = message, CompactDetail = message, DataState = ProviderDataState.NoQuota };
            }
            if (account.Scope != current?.Scope)
            {
                Reset();
                current = account;
            }
            else if (account.Revision != current?.Revision)
            {
                Cancel();
                attempted = DateTimeOffset.MinValue;
                current = account;
            }
            if (pending is { IsCompleted: true })
            {
                var result = pending.GetAwaiter().GetResult();
                pending = null;
                cancellation?.Dispose(); cancellation = null;
                error = result.Error;
                if (error.Length == 0)
                {
                    if (result.FiveHour is not null || result.Weekly is not null || result.Extra?.Count > 0)
                    { cached = result; succeeded = clock.GetUtcNow(); }
                    else error = "官方尚未返回可用额度窗口";
                }
            }
            if (pending is null && (force || clock.GetUtcNow() - attempted >= TimeSpan.FromSeconds(60)))
            {
                force = false;
                attempted = clock.GetUtcNow();
                cancellation = new();
                pending = Read(account, cancellation.Token);
            }
            local = local with { Tier = account.Tier, QuotaScope = account.Scope };
            if (cached is { } value)
            {
                var stale = error.Length > 0 || clock.GetUtcNow() - succeeded > TimeSpan.FromMinutes(3);
                QuotaWindow? Mark(QuotaWindow? window) => window is null ? null : window with { IsStale = stale };
                return local with
                {
                    FiveHour = Mark(value.FiveHour), Weekly = Mark(value.Weekly),
                    ExtraQuotas = (value.Extra ?? []).Select(x => x with { Window = Mark(x.Window)! }).ToArray(),
                    DataState = stale ? ProviderDataState.Stale : ProviderDataState.Available,
                    Detail = error.Length > 0 ? error + "；保留本账号上次成功回报。" : "Claude 官方额度；每 60 秒自动查询，手动刷新可立即重试。",
                    CompactDetail = "",
                    Metadata = local.Metadata with { Sources = local.Metadata.Sources.Concat(["https://api.anthropic.com/api/oauth/usage"]).ToArray() }
                };
            }
            var detail = error.Length > 0 ? error : "正在查询 Claude 官方额度";
            return local with { Detail = detail, CompactDetail = detail,
                DataState = error.Length > 0 ? ProviderDataState.ReadFailed : ProviderDataState.NoQuota };
        }
    }

    private async Task<ClaudeDesktopQuotaResult> Read(ClaudeDesktopQuotaAccount account, CancellationToken token)
    {
        try { return await client.ReadAsync(account, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(Error: "Claude 额度查询已取消或超时"); }
        catch { return new(Error: "Claude 官方额度查询失败，请检查网络后刷新"); }
    }

    public void Invalidate() { lock (sync) force = true; }
    private void Cancel() { cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; pending = null; }
    private void Reset() { Cancel(); current = null; cached = null; error = ""; attempted = succeeded = DateTimeOffset.MinValue; }
    public void Dispose() { lock (sync) { disposed = true; Reset(); } }
}
