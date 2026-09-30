using System.Text.Json;

namespace VibeGauge.Core;

public static class OfficialQuotaParser
{
    private static double? Number(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out number) && double.IsFinite(number) ? number : null;
    }
    private static DateTimeOffset? Epoch(double? value)
    {
        if (value > 1e12) value /= 1000;
        return value is > 1e9 and < 4.2e9 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(value.Value * 1000)) : null;
    }
    private static QuotaWindow? Window(double? pct, DateTimeOffset? reset, DateTimeOffset now, int hours) =>
        pct is { } p && double.IsFinite(p) ? new((int)Math.Round(Math.Clamp(p, 0, 100)), reset, now, TimeSpan.FromHours(hours)) : null;

    public static PlatformStatus Provider(string host, string title, JsonElement root, DateTimeOffset now)
    {
        var status = new PlatformStatus(title, "官方接口", false, 0, ProviderDataState.Available, "官方回报");
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) ||
            root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            return status with { DataState = ProviderDataState.ReadFailed, Detail = "官方接口返回业务错误" };
        if (host is "open.bigmodel.cn" or "api.z.ai")
        {
            if (root.StringOrEmpty("msg").Replace(" ", "").Contains("不存在CodingPlan", StringComparison.OrdinalIgnoreCase))
                return status with { DataState = ProviderDataState.NoQuota, Detail = "此 key 没有 Coding Plan 订阅" };
            if (Number(root, "code") != 200)
                return status with { DataState = ProviderDataState.ReadFailed, Detail = "官方接口返回业务错误" };
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return status with { DataState = ProviderDataState.ReadFailed, Detail = "官方接口未返回有效额度" };
            var limits = data.TryGetProperty("limits", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Where(x => Number(x, "percentage") is not null).OrderBy(x => Number(x, "nextResetTime") ?? 0).ToArray() : [];
            if (limits.Length == 0) return arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() == 0
                ? status with { DataState = ProviderDataState.NoQuota, Detail = "未检测到 Coding Plan 订阅" }
                : status with { DataState = ProviderDataState.ReadFailed, Detail = "官方接口未返回有效额度" };
            QuotaWindow? Read(JsonElement value, int hours) => Window(Number(value, "percentage"), Epoch(Number(value, "nextResetTime")), now, hours);
            return status with { FiveHour = limits.Length > 1 ? Read(limits[0], 5) : null, Weekly = Read(limits[^1], 168) };
        }
        if (host == "api.minimaxi.com")
        {
            if (root.TryGetProperty("base_resp", out var response) && Number(response, "status_code") != 0 ||
                root.TryGetProperty("code", out _) && Number(root, "code") is not (0 or 200))
                return status with { DataState = ProviderDataState.ReadFailed, Detail = "官方接口返回业务错误" };
            if (!root.TryGetProperty("data", out var data)) return status with { DataState = ProviderDataState.ReadFailed, Detail = "官方接口未返回有效额度" };
            if (data.ValueKind == JsonValueKind.Null && (Number(root, "code") is 0 or 200 || root.TryGetProperty("base_resp", out var ok) && Number(ok, "status_code") == 0))
                return status with { DataState = ProviderDataState.NoQuota, Detail = "未检测到 Token Plan" };
            if (data.ValueKind != JsonValueKind.Object) return status with { DataState = ProviderDataState.ReadFailed, Detail = "官方接口未返回有效额度" };
            var total = Number(data, "current_interval_total_count");
            var week = Number(data, "current_weekly_total_count");
            var parsed = status with
            {
                Tier = "Token Plan",
                FiveHour = Window(total > 0 ? Number(data, "current_interval_usage_count") / total * 100 : null,
                    Number(data, "remains_time") is >= 0 and < 3.2e10 and var remaining ? now.AddMilliseconds(remaining) : null, now, 5),
                Weekly = Window(week > 0 ? Number(data, "current_weekly_usage_count") / week * 100 : null, Epoch(Number(data, "weekly_end_time")), now, 168)
            };
            return parsed.FiveHour is null && parsed.Weekly is null
                ? parsed with { DataState = ProviderDataState.ReadFailed, Detail = "官方接口未返回有效额度" } : parsed;
        }
        double? balance = null;
        var currency = host == "openrouter.ai" ? "USD" : "CNY";
        if (host == "api.deepseek.com" && root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array && infos.GetArrayLength() > 0)
        { balance = Number(infos[0], "total_balance"); currency = infos[0].StringOrEmpty("currency"); }
        else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            balance = Number(data, host == "openrouter.ai" ? "limit_remaining" : "available_balance");
        return status with { DataState = balance is null ? ProviderDataState.ReadFailed : ProviderDataState.Available, Tier = "API 余额", Detail = balance is { } amount ? $"{(host == "openrouter.ai" ? "Key 剩余额度" : "余额")} {amount:0.00} {currency}" : "官方接口未返回可用余额" };
    }

    public static PlatformStatus Cli(string title, JsonElement root, DateTimeOffset now)
    {
        var status = new PlatformStatus(title, "官方 CLI", false, 0, ProviderDataState.Available, "官方 CLI 回报");
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)) return status with { DataState = ProviderDataState.ReadFailed, Detail = "官方 CLI 返回错误" };
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var selected = items.EnumerateArray().FirstOrDefault(x => x.StringOrEmpty("product") is "coding-plan" or "");
            if (selected.ValueKind != JsonValueKind.Object) return status with { DataState = ProviderDataState.NoQuota, Detail = "未订阅 Coding Plan" };
            if (selected.TryGetProperty("subscribed", out var subscribed) && subscribed.ValueKind == JsonValueKind.False)
                return status with { DataState = ProviderDataState.NoQuota, Detail = "未订阅 Coding Plan" };
            if (selected.TryGetProperty("error", out _)) return status with { DataState = ProviderDataState.ReadFailed, Detail = "官方 CLI 返回错误" };
            if (selected.TryGetProperty("periods", out var periods) && periods.ValueKind == JsonValueKind.Array)
                foreach (var p in periods.EnumerateArray())
                {
                    var label = p.StringOrEmpty("label");
                    var w = Window(Number(p, "percent"), Formatting.ParseDate(p.StringOrEmpty("reset_at")), now,
                        label == "weekly" ? 168 : label == "monthly" ? 720 : 5);
                    status = label switch { "session" or "5h" => status with { FiveHour = w }, "weekly" => status with { Weekly = w }, "monthly" => status with { Monthly = w }, _ => status };
                }
            return status;
        }
        QuotaWindow? Read(string key, int hours)
        {
            if (!root.TryGetProperty(key, out var item) || item.ValueKind != JsonValueKind.Object) return null;
            var ratio = Number(item, "percentage");
            if (ratio is null && Number(item, "totalQuota") is > 0 and var total) ratio = Number(item, "usedQuota") / total;
            return Window(ratio * 100, Epoch(Number(item, "resetTime")), now, hours);
        }
        status = status with { FiveHour = Read("per5Hour", 5), Weekly = Read("perWeek", 168), Monthly = Read("perBillMonth", 720) };
        return status.FiveHour is null && status.Weekly is null && status.Monthly is null
            ? status with { DataState = ProviderDataState.NoQuota, Detail = "未检测到 Coding Plan 订阅" } : status;
    }

    public static PlatformStatus Kimi(JsonElement envelope, DateTimeOffset now)
    {
        var empty = new PlatformStatus("Kimi Code", "已登录", true, 0, ProviderDataState.ReadFailed, "官方额度接口暂不可用");
        if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            data.StringOrEmpty("kind") != "ok" || envelope.Long("code") != 0 ||
            !data.TryGetProperty("quota", out var quota) || quota.ValueKind != JsonValueKind.Object || !quota.TryGetProperty("usages", out var usages)) return empty;
        var entries = usages.ValueKind == JsonValueKind.Object ? usages.EnumerateObject().ToDictionary(x => x.Name, x => x.Value) :
            usages.ValueKind == JsonValueKind.Array ? usages.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .GroupBy(x => x.StringOrEmpty("name") is { Length: > 0 } name ? name : x.StringOrEmpty("window"))
                .ToDictionary(x => x.Key, x => x.Last()) : new Dictionary<string, JsonElement>();
        QuotaWindow? Read(string key, TimeSpan span)
        {
            if (!entries.TryGetValue(key, out var entry) || entry.DoubleOrNull("usedRatio") is not { } ratio || !double.IsFinite(ratio)) return null;
            return new((int)Math.Round(Math.Clamp(ratio, 0, 1) * 100), Formatting.ParseDate(entry.StringOrEmpty("resetAt")), now, span);
        }
        return empty with
        {
            DataState = ProviderDataState.Available,
            Detail = "官方本机服务回报",
            Tier = "Kimi Code",
            FiveHour = Read("limit5h", TimeSpan.FromHours(5)),
            Weekly = Read("limit7d", TimeSpan.FromDays(7)),
            Monthly = Read("monthTotal", TimeSpan.FromDays(30)) ?? Read("monthCode", TimeSpan.FromDays(30))
        };
    }
}
