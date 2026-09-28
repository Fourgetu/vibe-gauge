using System.Globalization;
using System.Net;
using System.Text.Json;

namespace VibeGauge.Core;

public static class Sub2ApiEndpoint
{
    public static string Normalize(string input)
    {
        input = input.Trim();
        if (input.Length is 0 or > 1500 || input.Any(char.IsControl) || input.Contains('\\') ||
            !Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Host.Length == 0 ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("请输入完整站点地址，不要包含账号、查询参数或锚点");
        var loopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.DnsSafeHost, out var ip) && IPAddress.IsLoopback(ip);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && loopback))
            throw new ArgumentException("远程站点必须使用 HTTPS；HTTP 仅允许本机地址");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/v1/usage", StringComparison.Ordinal))
            path += path.EndsWith("/v1", StringComparison.Ordinal) ? "/usage" : "/v1/usage";
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }

    public static Uri UsageUri(string endpoint) => new(Normalize(endpoint));
}

public static class Sub2ApiParser
{
    // Schema: Wei-Shaw/sub2api, backend/internal/handler/gateway_handler.go,
    // Usage / usageQuotaLimited / usageUnrestricted (verified 2026-09-28).
    public static PlatformStatus Parse(string title, JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object || Child(root, "isValid").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("Unsupported sub2api usage response");
        if (!Child(root, "isValid").GetBoolean())
            return Failure(title, "站点报告密钥不可用，请检查密钥状态");
        var mode = Text(root, "mode");
        if (mode is not ("quota_limited" or "unrestricted" or ""))
            throw new JsonException("Unsupported sub2api mode");
        if (mode.Length == 0 && Number(root, "balance") is null && Number(root, "remaining") is null)
            throw new JsonException("Missing sub2api usage fields");
        var quota = Child(root, "quota");
        var subscription = Child(root, "subscription");
        var details = new List<string>();
        var unit = Text(root, "unit");
        if (unit.Length == 0) unit = Text(quota, "unit");
        if (unit.Length == 0) unit = "USD";
        if (unit.Length > 12 || unit.Any(char.IsControl)) throw new JsonException("Invalid currency unit");
        string Money(double value) => value.ToString("0.00##", CultureInfo.InvariantCulture) + " " + unit;
        if (Number(root, "balance") is { } balance) details.Add("钱包余额 " + Money(balance));
        else if (Number(quota, "remaining") is { } remaining) details.Add("Key 剩余额度 " + Money(remaining));
        else if (Number(root, "remaining") is { } available)
            details.Add(available < 0 ? "站点未设置额度上限" : "剩余可用 " + Money(available));
        if (Number(quota, "used") is { } used && Number(quota, "limit") is { } limit)
            details.Add($"累计已用 {Money(used)} / {Money(limit)}");

        QuotaWindow? five = null, daily = null, weekly = null, monthly = null;
        var rates = Child(root, "rate_limits");
        if (rates.ValueKind == JsonValueKind.Array)
            foreach (var rate in rates.EnumerateArray())
            {
                var window = Text(rate, "window");
                var span = window switch { "5h" => TimeSpan.FromHours(5), "1d" => TimeSpan.FromDays(1), "7d" => TimeSpan.FromDays(7), _ => TimeSpan.Zero };
                if (span == TimeSpan.Zero) continue;
                var meter = Meter(rate, "used", "limit", Date(rate, "reset_at"), span, now);
                if (window == "5h") five = meter;
                else if (window == "1d") daily = meter;
                else weekly = meter;
            }
        if (subscription.ValueKind == JsonValueKind.Object)
        {
            daily ??= Meter(subscription, "daily_usage_usd", "daily_limit_usd", null, TimeSpan.FromDays(1), now);
            var weekStart = Date(subscription, "weekly_window_start");
            var weekReset = weekStart <= DateTimeOffset.MaxValue.AddDays(-7) ? weekStart?.AddDays(7) : null;
            weekly ??= Meter(subscription, "weekly_usage_usd", "weekly_limit_usd", weekReset, TimeSpan.FromDays(7), now);
            monthly = Meter(subscription, "monthly_usage_usd", "monthly_limit_usd", null, TimeSpan.FromDays(30), now);
            foreach (var (prefix, label) in new[] { ("daily", "日"), ("weekly", "周"), ("monthly", "月") })
                if (Number(subscription, prefix + "_usage_usd") is { } spent)
                    details.Add(label + "用量 " + Money(spent) +
                        (Number(subscription, prefix + "_limit_usd") is { } cap && cap > 0 ? " / " + Money(cap) : " · 未返回有效上限"));
        }
        var usage = Child(root, "usage");
        foreach (var (period, label) in new[] { ("today", "今日"), ("total", "累计") })
        {
            var summary = Child(usage, period);
            var parts = new List<string>();
            if (Number(summary, "actual_cost") is { } cost) parts.Add(Money(cost));
            if (Number(summary, "requests") is { } requests) parts.Add(requests.ToString("0", CultureInfo.InvariantCulture) + " 次");
            if (Number(summary, "total_tokens") is { } tokens) parts.Add(tokens.ToString("N0", CultureInfo.InvariantCulture) + " tokens");
            if (parts.Count > 0) details.Add(label + " " + string.Join(" · ", parts));
        }
        var expiry = Date(root, "expires_at") ?? Date(subscription, "expires_at");
        if (expiry is { } expires) details.Add((expires <= now ? "已到期 " : "到期 ") + expires.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
        var state = Text(root, "status");
        if (state == "quota_exhausted") details.Add("Key 总额度已用尽");
        else if (state == "expired") details.Add("Key 已过期");
        if (details.Count == 0 && five is null && daily is null && weekly is null && monthly is null)
            details.Add("连接成功；站点未返回余额或用量");
        return new(title, "sub2api", false, 0, ProviderDataState.Available,
            string.Join("\n", details), five, weekly, Monthly: monthly, Daily: daily, AlwaysShowDetail: true);
    }

    public static PlatformStatus Failure(string title, string message) =>
        new(title, "sub2api", false, 0, ProviderDataState.ReadFailed, message, AlwaysShowDetail: true);

    private static QuotaWindow? Meter(JsonElement root, string usedField, string limitField, DateTimeOffset? reset, TimeSpan span, DateTimeOffset now)
    {
        if (Number(root, usedField) is not { } used || Number(root, limitField) is not { } limit || limit <= 0 || used < 0) return null;
        return new((int)Math.Clamp(Math.Round(used / limit * 100), 0, 100), reset, now, span);
    }

    private static JsonElement Child(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string Text(JsonElement root, string name) => Child(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";
    private static double? Number(JsonElement root, string name) =>
        Child(root, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static DateTimeOffset? Date(JsonElement root, string name) =>
        DateTimeOffset.TryParse(Text(root, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
