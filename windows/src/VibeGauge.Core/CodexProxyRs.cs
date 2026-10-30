using System.Globalization;
using System.Text.Json;

namespace VibeGauge.Core;

public static class CodexProxyRsEndpoint
{
    public static string Normalize(string address) => Sub2ApiEndpoint.Normalize(address)[..^"/v1/usage".Length];
    public static Uri At(string address, string path) => new(Normalize(address) + "/" + path);
}

public sealed record RemoteUsageMetrics(long Requests, long Input, long Output, long Cached, long CacheWrite,
    long Reasoning, long Total, decimal? CostUsd, bool CostIncomplete)
{
    public static RemoteUsageMetrics Sum(IEnumerable<RemoteUsageMetrics> source)
    {
        var values = source.ToArray();
        return new(values.Sum(x => x.Requests), values.Sum(x => x.Input), values.Sum(x => x.Output),
            values.Sum(x => x.Cached), values.Sum(x => x.CacheWrite), values.Sum(x => x.Reasoning), values.Sum(x => x.Total),
            values.Any(x => x.CostUsd is not null) ? values.Sum(x => x.CostUsd ?? 0) : null,
            values.Any(x => x.CostIncomplete || x.CostUsd is null && x.Requests > 0));
    }
}
public sealed record RemoteUsageBucket(DateTimeOffset Start, int Seconds, string Label, RemoteUsageMetrics Metrics)
{
    public DateTimeOffset End => Start.AddSeconds(Seconds);
}
public sealed record RemoteUsagePeriod(DateTimeOffset Start, DateTimeOffset End, RemoteUsageMetrics Summary,
    IReadOnlyList<RemoteUsageBucket> Buckets, string AnalyticsState);
public sealed record RemoteUsageCall(string Id, string Time, string Model, string Status, long? Tokens, long? LatencyMs);
public sealed record RemoteUsageReport(DateTimeOffset CapturedAt, RemoteUsagePeriod Today, RemoteUsagePeriod Recent,
    IReadOnlyList<RemoteUsageBucket> Retained, IReadOnlyList<RemoteUsageCall> Calls, string Note = "")
{
    public RemoteUsageMetrics Cumulative => RemoteUsageMetrics.Sum(Retained.Select(x => x.Metrics));
}

public static class CodexProxyRsParser
{
    public const string Kind = "Codex Proxy RS";
    // zyycn/codex-proxy-rs v3.35.1: openai/usage.rs and key_usage/presenter.rs.
    public static PlatformStatus Budget(string title, JsonElement root, DateTimeOffset now)
    {
        if (Text(root, "unit") != "USD") throw new JsonException("Unsupported currency");
        var lines = new List<string>();
        QuotaWindow? Window(string field, string label, TimeSpan span)
        {
            var window = Required(root, field);
            var used = Money(window, "used") ?? throw new JsonException("Missing usage");
            var limit = Money(window, "total");
            var remaining = Money(window, "remaining");
            var reset = OptionalDate(window, "resetsAt");
            lines.Add($"{label}已用 {used:0.00####} USD" + (limit is > 0 ? $" / {limit:0.00####} USD" : " · 不限额") +
                (remaining is { } left ? $" · 剩余 {left:0.00####} USD" : ""));
            return limit is > 0 ? new((int)Math.Clamp(Math.Round(used / limit.Value * 100), 0, 100), reset, now, span) : null;
        }
        var daily = Window("daily", "日额度", TimeSpan.FromDays(1));
        var weekly = Window("weekly", "周额度", TimeSpan.FromDays(7));
        return new(title, Kind, false, 0, ProviderDataState.Available, string.Join("\n", lines),
            Weekly: weekly, Daily: daily, AlwaysShowDetail: true, CompactDetail: string.Join("\n", lines));
    }

    public static JsonElement Data(JsonElement root)
    {
        if (Child(root, "code").ValueKind != JsonValueKind.Number || !Child(root, "code").TryGetInt32(out var code) || code != 200)
            throw new JsonException("API envelope rejected");
        return Required(root, "data");
    }
    public static RemoteUsagePeriod Overview(JsonElement root)
    {
        var data = Data(root);
        var start = Date(data, "startTime"); var end = Date(data, "endTime");
        if (end < start || end - start > TimeSpan.FromDays(31)) throw new JsonException("Invalid range");
        var summary = Metrics(Required(data, "summary"));
        var trend = Child(data, "trend");
        if (trend.ValueKind != JsonValueKind.Array || trend.GetArrayLength() > 4000) throw new JsonException("Missing trend");
        var buckets = trend.EnumerateArray().Select(x => new RemoteUsageBucket(Date(x, "time"),
            checked((int)Count(x, "bucketSeconds")), Text(x, "label"), Metrics(x))).OrderBy(x => x.Start).ToArray();
        for (var i = 0; i < buckets.Length; i++)
            if (buckets[i].Seconds is < 1 or > 172800 || buckets[i].Start > DateTimeOffset.MaxValue.AddSeconds(-buckets[i].Seconds) ||
                buckets[i].Start < start || buckets[i].Start > end ||
                i > 0 && buckets[i - 1].End > buckets[i].Start) throw new JsonException("Invalid trend intervals");
        return new(start, end, summary, buckets, Text(Child(data, "analytics"), "state"));
    }
    public static IReadOnlyList<RemoteUsageCall> Records(JsonElement root)
    {
        var items = Child(Data(root), "items");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100) throw new JsonException("Invalid records");
        return items.EnumerateArray().Select(x => new RemoteUsageCall(Text(x, "id"), Text(x, "createdAtDisplay"),
            Text(x, "model"), Text(x, "status"), OptionalCount(Child(x, "tokenDetails"), "totalTokens"), OptionalCount(x, "latencyMs"))).ToArray();
    }
    public static RemoteUsageMetrics Metrics(JsonElement root) => new(Count(root, "requests"), Count(root, "inputTokens"),
        Count(root, "outputTokens"), Count(root, "cachedInputTokens"), Count(root, "cacheWriteInputTokens"),
        Count(root, "reasoningOutputTokens"), Count(root, "totalTokens"), Money(root, "costUsd"),
        Child(root, "costIncomplete").ValueKind == JsonValueKind.True);
    public static PlatformStatus Failure(string title, string message) => new(title, Kind, false, 0,
        ProviderDataState.ReadFailed, message, AlwaysShowDetail: true);
    public static JsonElement Child(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var child) ? child : default;
    private static JsonElement Required(JsonElement root, string name) => Child(root, name) is { ValueKind: JsonValueKind.Object } child ? child : throw new JsonException("Missing object");
    public static string Text(JsonElement root, string name) => Child(root, name) is { ValueKind: JsonValueKind.String } child
        ? new string((child.GetString() ?? "").Where(x => !char.IsControl(x)).Take(256).ToArray()) : "";
    private static long Count(JsonElement root, string name) => OptionalCount(root, name) ?? throw new JsonException("Missing token count");
    private static long? OptionalCount(JsonElement root, string name)
    {
        var v = Child(root, name);
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var count) && count >= 0) return count;
        throw new JsonException("Invalid count");
    }
    private static decimal? Money(JsonElement root, string name)
    {
        var v = Child(root, name);
        if (v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var number) && number >= 0) return number;
        throw new JsonException("Invalid money");
    }
    private static DateTimeOffset Date(JsonElement root, string name) => OptionalDate(root, name) ?? throw new JsonException("Missing date");
    private static DateTimeOffset? OptionalDate(JsonElement root, string name)
    {
        var v = Child(root, name);
        if (v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var date)) return date;
        throw new JsonException("Invalid date");
    }
}

public static class RemoteUsageHistory
{
    // Replace complete overlapping server buckets, never add repeated rolling totals.
    // Only keep fully elapsed older buckets when the server changes granularity.
    public static IReadOnlyList<RemoteUsageBucket> Merge(IEnumerable<RemoteUsageBucket> previous, RemoteUsagePeriod current) =>
        previous.Where(x => x.End <= current.Start).Concat(current.Buckets).OrderBy(x => x.Start).ToArray();
}
