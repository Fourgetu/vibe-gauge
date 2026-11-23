using System.Text.Json;

namespace VibeGauge.Core;

public sealed record AntigravityQuota(QuotaWindow? GeminiFive = null, QuotaWindow? GeminiWeek = null,
    QuotaWindow? OtherFive = null, QuotaWindow? OtherWeek = null, string Error = "", string Tier = "")
{
    public static AntigravityQuota? FromStatusLine(JsonElement root, DateTimeOffset observed)
    {
        var tier = root.StringOrEmpty("plan_tier").Trim().ToLowerInvariant() switch
        {
            "google ai ultra" or "ultra" => "Ultra", "google ai pro" or "pro" => "Pro",
            "google ai plus" or "plus" => "Plus", "free" or "google ai free" => "Free", _ => ""
        };
        if (tier.Length == 0 || !root.TryGetProperty("quota", out var quota) || quota.ValueKind != JsonValueKind.Object) return null;
        QuotaWindow? Window(string id) => quota.TryGetProperty(id, out var bucket) ? ReadWindow(id, bucket, observed) : null;
        var result = new AntigravityQuota(Window("gemini-5h"), Window("gemini-weekly"), Window("3p-5h"), Window("3p-weekly"), Tier: tier);
        return result.GeminiFive == null && result.GeminiWeek == null && result.OtherFive == null && result.OtherWeek == null ? null : result;
    }

    private static QuotaWindow? ReadWindow(string id, JsonElement bucket, DateTimeOffset observed)
    {
        if (bucket.ValueKind != JsonValueKind.Object || id is not ("gemini-5h" or "gemini-weekly" or "3p-5h" or "3p-weekly") ||
            bucket.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True ||
            !bucket.TryGetProperty("remaining_fraction", out var fraction) || fraction.ValueKind != JsonValueKind.Number ||
            !fraction.TryGetDouble(out var remaining) || !double.IsFinite(remaining) || remaining is < 0 or > 1) return null;
        var rawReset = bucket.StringOrEmpty("reset_time");
        var reset = Formatting.ParseDate(rawReset);
        if (rawReset.Length > 0 && reset is null || reset <= observed) return null;
        return new(100 - (int)Math.Round(remaining * 100, MidpointRounding.AwayFromZero), reset, observed,
            id.EndsWith("weekly", StringComparison.Ordinal) ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5));
    }

    public static AntigravityQuota Parse(string json, DateTimeOffset observed)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.StringOrEmpty("status") != "SUCCESS" || !root.TryGetProperty("command", out var command) ||
            command.StringOrEmpty("name") != "usage" || !command.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return new(Error: "agy 未返回有效的额度报告");
        var windows = new Dictionary<string, QuotaWindow>();
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("buckets", out var buckets) ||
                buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) continue;
                var id = bucket.StringOrEmpty("id");
                if (ReadWindow(id, bucket, observed) is { } window) windows[id] = window;
            }
        }
        return windows.Count == 0 ? new(Error: "agy 未返回有效的额度窗口") :
            new(windows.GetValueOrDefault("gemini-5h"), windows.GetValueOrDefault("gemini-weekly"),
                windows.GetValueOrDefault("3p-5h"), windows.GetValueOrDefault("3p-weekly"));
    }
}

public sealed class AntigravityQuotaRefresh(Func<CancellationToken, Task<AntigravityQuota>> query, TimeProvider? time = null) : IDisposable
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task<AntigravityQuota>? pending;
    private DateTimeOffset next;
    private AntigravityQuota? result;
    private bool discard, disposed;

    public PlatformStatus Apply(PlatformStatus local)
    {
        lock (gate)
        {
            if (disposed) return local;
            var now = clock.GetUtcNow();
            if (pending is { IsCompleted: true })
            {
                if (!discard) result = pending.GetAwaiter().GetResult();
                pending = null;
                discard = false;
            }
            if (pending is null && now >= next)
            {
                next = now.AddMinutes(3);
                var token = lifetime.Token;
                pending = Task.Run(async () =>
                {
                    try { return await query(token).ConfigureAwait(false); }
                    catch { return new AntigravityQuota(Error: "无法读取 agy 额度，请检查登录与网络"); }
                });
            }
            QuotaWindow? Fresh(QuotaWindow? w) => w is not null && w.CapturedAt >= now.AddMinutes(-4)
                && (w.ResetsAt is null || w.ResetsAt > now) ? w : null;
            var five = Fresh(result?.GeminiFive);
            var week = Fresh(result?.GeminiWeek);
            var otherFive = Fresh(result?.OtherFive);
            var otherWeek = Fresh(result?.OtherWeek);
            bool available = five != null || week != null || otherFive != null || otherWeek != null;
            var note = available ? "agy 当前登录账号；桌面与 CLI 登录不同时，以 CLI 账号为准" :
                result?.Error is { Length: > 0 } error ? error : "等待 agy 后台额度回报";
            return local with { FiveHour = five, Weekly = week, SecondaryFiveHour = otherFive,
                SecondaryWeekly = otherWeek, SecondaryPoolName = "Claude/GPT",
                Tier = available ? result?.Tier ?? "" : "",
                Metadata = new([.. local.Metadata?.Rows ?? [], new("额度来源", note)],
                    [.. local.Metadata?.Sources ?? [], available && result?.Tier.Length > 0 ? "agy statusLine: plan_tier, quota" : "agy --print /usage --output-format json"]) };
        }
    }

    public void Invalidate()
    {
        lock (gate) { result = null; discard = pending is not null; next = default; }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; lifetime.Cancel(); lifetime.Dispose(); }
    }
}
