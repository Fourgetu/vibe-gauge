namespace VibeGauge.Core;

// Only opaque account/credential fingerprints cross this boundary, never bearer tokens or session keys.
public sealed record ClaudeDesktopQuotaAccount(string Profile, string Scope, string Revision, string Tier, string SourceKind = "oauth");
public sealed record ClaudeDesktopQuotaAccess(ClaudeDesktopQuotaAccount? Account = null, string Error = "", bool Disabled = false);
public sealed record ClaudeDesktopQuotaResult(QuotaWindow? FiveHour = null, QuotaWindow? Weekly = null,
    IReadOnlyList<NamedQuota>? Extra = null, string Error = "", bool RateLimited = false);

public interface IClaudeDesktopQuotaClient
{
    ClaudeDesktopQuotaAccess ReadAccount(string profile);
    Task<ClaudeDesktopQuotaResult> ReadAsync(ClaudeDesktopQuotaAccount account, CancellationToken cancellationToken);
}

public sealed class ClaudeDesktopQuotaRefresh(IClaudeDesktopQuotaClient client, TimeProvider? time = null) : IDisposable
{
    public static readonly TimeSpan BasePollInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaxBackoffInterval = TimeSpan.FromMinutes(30);
    private readonly object sync = new();
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private ClaudeDesktopQuotaAccount? current;
    private ClaudeDesktopQuotaResult? cached;
    private Task<ClaudeDesktopQuotaResult>? pending;
    private CancellationTokenSource? cancellation;
    private DateTimeOffset attempted = DateTimeOffset.MinValue;
    private DateTimeOffset succeeded = DateTimeOffset.MinValue;
    private int consecutiveFailures;
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
            catch { access = new(Error: "无法读取 Claude 额度来源配置，请稍后刷新"); }
            if (access.Disabled)
            {
                Reset();
                var note = access.Error.Length > 0 ? access.Error : "仅本地用量统计（可在「系统 → Claude 额度来源」手动开启额度同步）";
                return local with
                {
                    Detail = string.IsNullOrWhiteSpace(local.Detail) ? note : local.Detail + "\n" + note,
                    DataState = ProviderDataState.NoQuota
                };
            }
            if (access.Account is not { } account)
            {
                Reset();
                var message = access.Error.Length > 0 ? access.Error : "Claude 额度凭据暂不可用，请在「系统 → Claude 额度来源」检查配置";
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
                consecutiveFailures = 0;
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
                    { cached = result; succeeded = clock.GetUtcNow(); consecutiveFailures = 0; }
                    else { error = "官方尚未返回可用额度窗口"; consecutiveFailures = Math.Min(consecutiveFailures + 1, 5); }
                }
                else
                {
                    consecutiveFailures = Math.Min(consecutiveFailures + (result.RateLimited ? 2 : 1), 5);
                }
            }
            var pollDelay = CurrentInterval();
            if (pending is null && (force || clock.GetUtcNow() - attempted >= pollDelay))
            {
                force = false;
                attempted = clock.GetUtcNow();
                cancellation = new();
                pending = Read(account, cancellation.Token);
            }
            local = local with { Tier = account.Tier, QuotaScope = account.Scope };
            var sourceUrl = account.SourceKind == "web"
                ? "https://claude.ai/api/organizations/*/usage"
                : "https://api.anthropic.com/api/oauth/usage";
            var sourceNote = account.SourceKind == "web"
                ? "Claude Web 额度（手动 sessionKey）；每 3 分钟自动查询，手动刷新可立即重试。"
                : "Claude OAuth 额度（~/.claude/.credentials.json）；每 3 分钟自动查询，手动刷新可立即重试。";
            if (cached is { } value)
            {
                var stale = error.Length > 0 || clock.GetUtcNow() - succeeded > TimeSpan.FromMinutes(9);
                QuotaWindow? Mark(QuotaWindow? window) => window is null ? null : window with { IsStale = stale };
                return local with
                {
                    FiveHour = Mark(value.FiveHour), Weekly = Mark(value.Weekly),
                    ExtraQuotas = (value.Extra ?? []).Select(x => x with { Window = Mark(x.Window)! }).ToArray(),
                    DataState = stale ? ProviderDataState.Stale : ProviderDataState.Available,
                    Detail = error.Length > 0 ? error + "；保留本账号上次成功回报。" : sourceNote,
                    CompactDetail = "",
                    Metadata = local.Metadata with { Sources = local.Metadata.Sources.Concat([sourceUrl]).ToArray() }
                };
            }
            var detail = error.Length > 0 ? error : "正在查询 Claude 订阅额度";
            return local with { Detail = detail, CompactDetail = detail,
                DataState = error.Length > 0 ? ProviderDataState.ReadFailed : ProviderDataState.NoQuota };
        }
    }

    private TimeSpan CurrentInterval()
    {
        if (consecutiveFailures <= 0) return BasePollInterval;
        var minutes = BasePollInterval.TotalMinutes * Math.Pow(2, consecutiveFailures);
        return TimeSpan.FromMinutes(Math.Min(minutes, MaxBackoffInterval.TotalMinutes));
    }

    private async Task<ClaudeDesktopQuotaResult> Read(ClaudeDesktopQuotaAccount account, CancellationToken token)
    {
        try { return await client.ReadAsync(account, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(Error: "Claude 额度查询已取消或超时"); }
        catch { return new(Error: "Claude 额度查询失败，请检查网络后刷新"); }
    }

    public void Invalidate() { lock (sync) { force = true; consecutiveFailures = 0; } }
    private void Cancel() { cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; pending = null; }
    private void Reset() { Cancel(); current = null; cached = null; error = ""; consecutiveFailures = 0; attempted = succeeded = DateTimeOffset.MinValue; }
    public void Dispose() { lock (sync) { disposed = true; Reset(); } }
}
