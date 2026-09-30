namespace VibeGauge.Core;

public sealed record CodexQuotaRequest(string Scope, string Tier, bool KnownPlan, DateTimeOffset AuthUpdatedAt);
public sealed record CodexQuotaResult(QuotaWindow? FiveHour = null, QuotaWindow? Weekly = null,
    IReadOnlyList<NamedQuota>? Extra = null, string Error = "");

public interface ICodexQuotaClient
{
    Task<CodexQuotaResult> ReadAsync(CodexQuotaRequest account, CancellationToken cancellationToken);
}

public sealed class CodexQuotaRefresh(ICodexQuotaClient client, TimeProvider? time = null) : IDisposable
{
    private readonly object sync = new();
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private Task<CodexQuotaResult>? pending;
    private CancellationTokenSource? cancellation;
    private CodexQuotaResult? cached;
    private string? scope;
    private string error = "";
    private DateTimeOffset attempted = DateTimeOffset.MinValue;
    private int force;
    private bool disposed;

    public PlatformStatus Apply(PlatformStatus local, CodexQuotaRequest? account)
    {
        lock (sync)
        {
            if (disposed) return local;
            if (scope != account?.Scope)
            {
                Cancel();
                scope = account?.Scope;
                cached = null;
                error = "";
                attempted = DateTimeOffset.MinValue;
            }
            if (account is null) return local;
            if (pending is { IsCompleted: true })
            {
                var result = pending.GetAwaiter().GetResult();
                pending = null;
                cancellation?.Dispose();
                cancellation = null;
                error = result.Error;
                if (error.Length == 0)
                {
                    if (result.FiveHour is not null || result.Weekly is not null || result.Extra?.Count > 0) cached = result;
                    else error = "官方尚未返回可用额度窗口";
                }
            }
            if (pending is null && (clock.GetUtcNow() - attempted >= TimeSpan.FromMinutes(1) || Volatile.Read(ref force) != 0))
            {
                Interlocked.Exchange(ref force, 0);
                attempted = clock.GetUtcNow();
                cancellation = new();
                pending = Read(account, cancellation.Token);
            }
            if (cached is { } value)
            {
                var captured = Latest(value.FiveHour, value.Weekly);
                if (Latest(local.FiveHour, local.Weekly) > captured) return local;
                return local with
                {
                    FiveHour = value.FiveHour, Weekly = value.Weekly, ExtraQuotas = value.Extra ?? [],
                    DataState = error.Length == 0 ? ProviderDataState.Available : ProviderDataState.Stale,
                    Detail = error.Length == 0 ? "当前账号官方额度；每 60 秒自动查询，手动刷新可立即重试。" : error + "；保留当前账号上次成功回报。",
                    CompactDetail = ""
                };
            }
            if (local.FiveHour is not null || local.Weekly is not null) return local;
            var message = error.Length > 0 ? error : "正在查询当前账号官方额度";
            return local with { Detail = message, CompactDetail = message };
        }
    }

    private async Task<CodexQuotaResult> Read(CodexQuotaRequest account, CancellationToken token)
    {
        try { return await client.ReadAsync(account, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(Error: "官方额度查询已取消"); }
        catch (Exception) { return new(Error: "官方额度查询失败，请检查登录和网络"); }
    }

    private static DateTimeOffset Latest(QuotaWindow? five, QuotaWindow? week) =>
        new[] { five?.CapturedAt ?? DateTimeOffset.MinValue, week?.CapturedAt ?? DateTimeOffset.MinValue }.Max();

    public void Invalidate() => Interlocked.Exchange(ref force, 1);

    private void Cancel()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        pending = null;
    }

    public void Dispose()
    {
        lock (sync) { disposed = true; Cancel(); }
    }
}
