using System.Globalization;
using System.Text.Json;

namespace VibeGauge.Core;

public sealed record CostEstimate(decimal Amount, string Currency, string AsOf, int UnpricedModels, bool Configured, int UnknownCalls = 0, string Source = "")
{
    public string Description => !Configured ? "未配置 prices.json · 不估算成本" :
        $"API 等价成本 {Currency} {Amount:N4}" + (UnpricedModels > 0 ? $" · {UnpricedModels} 个模型未定价" : "") +
        (UnknownCalls > 0 ? $" · {UnknownCalls} 次用量未知" : "") + "（非实际账单）" +
        (Source == "OpenRouter" ? $" · OpenRouter 基础 Token 单价 · {AsOf}" : "");
}

public sealed class PriceTable
{
    public sealed record Rate(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);
    public Dictionary<string, Rate> Rates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Currency { get; private set; } = "USD";
    public string AsOf { get; private set; } = "";
    public string Error { get; private set; } = "";
    public string Source { get; private set; } = "";
    public bool ExactMatch { get; private set; }
    private string[] stripSuffixes = [];
    public static PriceTable Load(AppPaths paths)
    {
        var file = paths.ResolveOwnDataFile("prices.json");
        try { return File.Exists(file) ? Parse(File.ReadAllText(file)) : new(); }
        catch (Exception e) when (e is IOException or JsonException or FormatException or InvalidOperationException or OverflowException) { return new() { Error = "prices.json 无效：" + e.Message }; }
    }
    public static PriceTable Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var table = new PriceTable();
        table.Source = doc.RootElement.StringOrEmpty("_source");
        table.ExactMatch = doc.RootElement.StringOrEmpty("_match") == "exact";
        if (doc.RootElement.TryGetProperty("_strip_suffixes", out var suffixes) && suffixes.ValueKind == JsonValueKind.Array)
            table.stripSuffixes = suffixes.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!).Where(x => x.Length is > 0 and <= 100).OrderByDescending(x => x.Length).ToArray();
        foreach (var item in doc.RootElement.EnumerateObject())
        {
            if (item.Name == "_currency") { table.Currency = item.Value.GetString() ?? "USD"; continue; }
            if (item.Name == "_asof") { table.AsOf = item.Value.GetString() ?? ""; continue; }
            if (item.Name.StartsWith('_')) continue;
            decimal Read(string key, decimal fallback = 0) => item.Value.TryGetProperty(key, out var value) && value.TryGetDecimal(out var n) && n >= 0 ? n : fallback;
            var input = Read("in");
            var rate = new Rate(input, Read("out"), Read("cache_read", input), Read("cache_write", input));
            if (rate.Input + rate.Output + rate.CacheRead + rate.CacheWrite > 0 || table.Source == "OpenRouter") table.Rates[item.Name] = rate;
        }
        return table;
    }
    public decimal? Cost(string model, long context, long output, long read, long write)
    {
        foreach (var suffix in stripSuffixes)
            if (model.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { model = model[..^suffix.Length]; break; }
        var rate = ExactMatch ? Rates.GetValueOrDefault(model) : Rates.Where(x => model.StartsWith(x.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Key.Length).Select(x => x.Value).FirstOrDefault();
        try { return rate is null ? null : (Math.Max(0m, (decimal)context - read - write) * rate.Input + (decimal)read * rate.CacheRead +
            (decimal)write * rate.CacheWrite + (decimal)output * rate.Output) / 1_000_000m; }
        catch (OverflowException) { return null; }
    }
    public CostEstimate Estimate(IEnumerable<ModelMix> models, int unknownCalls = 0)
    {
        decimal sum = 0;
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models)
            if (Cost(m.Model, m.Context, m.Output, m.CacheRead, m.CacheWrite) is { } cost) sum += cost;
            else missing.Add(m.Model);
        return new(sum, Currency, AsOf, missing.Count, Rates.Count > 0, unknownCalls, Source);
    }
}

public sealed record ApiRateLimit(string Header, string Value, DateTimeOffset CapturedAt)
{
    public DateTimeOffset? ResetAt
    {
        get
        {
            if (!Header.Contains("reset", StringComparison.OrdinalIgnoreCase) && !Header.Equals("retry-after", StringComparison.OrdinalIgnoreCase)) return null;
            if (Value.Contains(':') && DateTimeOffset.TryParse(Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date;
            if (double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0)
            {
                if (n is > 1e12 and < 4.2e12) return DateTimeOffset.FromUnixTimeMilliseconds((long)n);
                if (n is > 1e9 and < 4.2e9) return DateTimeOffset.FromUnixTimeSeconds((long)n);
                return n <= 86400 * 366 ? AddSeconds(n) : null;
            }
            var matches = System.Text.RegularExpressions.Regex.Matches(Value, @"(\d+(?:\.\d+)?)(ms|s|m|h|d)");
            if (matches.Count == 0 || string.Concat(matches.Select(x => x.Value)) != Value) return null;
            double seconds = 0;
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return null;
                seconds += n * (match.Groups[2].Value switch { "ms" => .001, "s" => 1, "m" => 60, "h" => 3600, "d" => 86400, _ => 0 });
            }
            return double.IsFinite(seconds) && seconds <= 86400 * 366 && seconds >= 0 ? AddSeconds(seconds) : null;
        }
    }
    private DateTimeOffset? AddSeconds(double seconds)
    {
        try { return CapturedAt.AddSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    public string DisplayValue => ResetAt is { } date ? Value + $" → {date.LocalDateTime:MM-dd HH:mm:ss}" : Value;
}
public sealed record ApiQuality(int P50, int P95, int Maximum, int RateLimited, IReadOnlyList<ApiRateLimit> Limits, NamedQuota? LimitingQuota = null)
{
    public static ApiQuality Build(IEnumerable<InteractionRecord> records, DateTimeOffset? now = null)
    {
        var rows = records.ToArray();
        var ms = rows.Where(x => x.LatencyMs > 0).Select(x => x.LatencyMs).Order().ToArray();
        int Percentile(double p) => ms.Length == 0 ? 0 : ms[Math.Clamp((int)Math.Ceiling(ms.Length * p) - 1, 0, ms.Length - 1)];
        var at = now ?? DateTimeOffset.Now;
        // A quota snapshot belongs to one account and one response; never splice headers.
        var latest = rows.Select(x => x.Source + "\n" + x.ApiHost).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() == 1
            ? rows.Where(x => x.Timestamp <= at && at - x.Timestamp <= TimeSpan.FromHours(1) && x.RateLimits?.Count > 0)
                .OrderByDescending(x => x.Timestamp).FirstOrDefault() : null;
        var limits = latest?.RateLimits?.Select(x => new ApiRateLimit(x.Key, x.Value, latest.Timestamp)).ToArray() ?? [];
        var windows = new List<NamedQuota>();
        foreach (var limit in limits.Where(x => x.Header.Contains("limit", StringComparison.OrdinalIgnoreCase) && !x.Header.Contains("remaining", StringComparison.OrdinalIgnoreCase)))
        {
            var marker = limit.Header.LastIndexOf("limit", StringComparison.OrdinalIgnoreCase);
            var prefix = limit.Header[..marker]; var suffix = limit.Header[(marker + 5)..];
            var remaining = limits.FirstOrDefault(x => x.Header.Equals(prefix + "remaining" + suffix, StringComparison.OrdinalIgnoreCase));
            var reset = limits.FirstOrDefault(x => x.Header.Equals(prefix + "reset" + suffix, StringComparison.OrdinalIgnoreCase));
            if (remaining is null || !double.TryParse(limit.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cap) ||
                !double.TryParse(remaining.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) ||
                !double.IsFinite(cap) || !double.IsFinite(left) || cap <= 0 || left < 0 || reset?.ResetAt <= at) continue;
            windows.Add(new(limit.Header, new((int)Math.Clamp(Math.Round((1 - left / cap) * 100), 0, 100), reset?.ResetAt,
                limit.CapturedAt, TimeSpan.FromHours(5))));
        }
        return new(Percentile(.5), Percentile(.95), ms.LastOrDefault(), rows.Count(x => x.Status == 429), limits,
            windows.OrderByDescending(x => x.Window.UsedPercent).FirstOrDefault());
    }
    public string Description => $"p50 {P50:N0} ms · p95 {P95:N0} ms · 最大 {Maximum:N0} ms · 429 {RateLimited} 次";
    public static Dictionary<string, string> SafeHeaders(IEnumerable<KeyValuePair<string, string>> headers) => headers
        .Where(x => AllowedHeader(x.Key) && x.Value.Length <= 160 && x.Value.All(c => c is >= ' ' and <= '~') &&
            (System.Text.RegularExpressions.Regex.IsMatch(x.Value.Trim(), @"^(?:\d+(?:\.\d+)?(?:ms|[smhd])?\s*){1,8}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)) ||
             x.Value.Contains(':') && DateTimeOffset.TryParse(x.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)))
        .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.First())
        .Take(32).ToDictionary(x => x.Key.ToLowerInvariant(), x => x.Value, StringComparer.OrdinalIgnoreCase);
    private static bool AllowedHeader(string name) => name.Equals("retry-after", StringComparison.OrdinalIgnoreCase) ||
        new[] { "x-ratelimit-", "ratelimit-", "anthropic-ratelimit-" }.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
        new[] { "limit", "remaining", "reset" }.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
}

public sealed class CodingPlan
{
    public string Label { get; init; } = "";
    public Dictionary<string, int> Requests { get; } = [];
    public Dictionary<string, string> Reset { get; } = [];
    public Dictionary<string, double> Weights { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CodingPlan> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Local;
    public int? SubscriptionDay { get; private set; }
    public static CodingPlan Parse(JsonElement root, string label, CodingPlan? parent = null)
    {
        var plan = new CodingPlan { Label = root.StringOrEmpty("plan") is { Length: > 0 } title ? title : label,
            Zone = parent?.Zone ?? TimeZoneInfo.Local, SubscriptionDay = parent?.SubscriptionDay };
        if (parent is not null && !root.TryGetProperty("reset", out _)) foreach (var r in parent.Reset) plan.Reset[r.Key] = r.Value;
        if (root.TryGetProperty("requests", out var limits) && limits.ValueKind == JsonValueKind.Object)
            foreach (var r in limits.EnumerateObject()) if (r.Value.ValueKind == JsonValueKind.Number && r.Value.TryGetInt32(out var n) && n > 0) plan.Requests[r.Name] = n;
        if (root.TryGetProperty("reset", out var resets) && resets.ValueKind == JsonValueKind.Object)
            foreach (var r in resets.EnumerateObject()) plan.Reset[r.Name] = r.Value.ValueKind == JsonValueKind.String ? r.Value.GetString() ?? "rolling" : "rolling";
        if (root.TryGetProperty("weights", out var weights) && weights.ValueKind == JsonValueKind.Object)
            foreach (var r in weights.EnumerateObject()) if (r.Value.ValueKind == JsonValueKind.Number && r.Value.TryGetDouble(out var n) && double.IsFinite(n) && n > 0) plan.Weights[r.Name] = n;
        if (DateOnly.TryParseExact(root.StringOrEmpty("subscribed_on"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) plan.SubscriptionDay = date.Day;
        if (root.StringOrEmpty("timezone") is { Length: > 0 } zone) plan.Zone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
            foreach (var model in models.EnumerateObject())
            {
                if (model.Value.ValueKind != JsonValueKind.Object) continue;
                var child = Parse(model.Value, model.Name, plan);
                if (child.Requests.Any(x => x.Key is "5h" or "weekly" or "monthly")) plan.Models[model.Name] = child;
            }
        return plan;
    }
    public double Weight(string model) => Weights.Where(x => x.Key != "*" && model.StartsWith(x.Key, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(x => x.Key.Length).Select(x => (double?)x.Value).FirstOrDefault() ?? Weights.GetValueOrDefault("*", 1);
    private string? Pool(string model) => Models.Keys.Where(x => model.StartsWith(x, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Length).FirstOrDefault();
    public IReadOnlyList<NamedQuota> Estimate(IEnumerable<InteractionRecord> records, DateTimeOffset now)
    {
        var calls = records.Where(x => x.Timestamp <= now && (x.ReachedUpstream ?? (x.Status != 0 && x.Status != 502))).ToArray();
        var result = new List<NamedQuota>();
        void Add(CodingPlan p, IEnumerable<InteractionRecord> rows, string prefix)
        {
            foreach (var (key, span) in new[] { ("5h", TimeSpan.FromHours(5)), ("weekly", TimeSpan.FromDays(7)), ("monthly", TimeSpan.FromDays(30)) })
                if (p.Requests.TryGetValue(key, out var limit))
                    result.Add(new(prefix + key + $" · {limit:N0} 请求额度", Window(rows.Select(x => (x.Timestamp, p.Weight(x.Model))),
                        p.Reset.GetValueOrDefault(key, "rolling"), span, limit, now, p.Zone, p.SubscriptionDay)));
        }
        Add(this, calls.Where(x => Pool(x.Model) is null), "共享池 · ");
        foreach (var (name, model) in Models) Add(model, calls.Where(x => Pool(x.Model) == name), name + " · ");
        return result;
    }
    public static QuotaWindow Window(IEnumerable<(DateTimeOffset At, double Weight)> calls, string kind, TimeSpan span,
        int limit, DateTimeOffset now, TimeZoneInfo zone, int? day = null)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var sorted = calls.Where(x => x.At <= now && x.Weight > 0 && double.IsFinite(x.Weight)).OrderBy(x => x.At).ToArray();
        DateTimeOffset? start = null, end = null;
        DateTimeOffset Local(DateTime date)
        {
            date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(date)) date = date.AddMinutes(1);
            return new(date, zone.GetUtcOffset(date));
        }
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        if (kind == "first_use")
        {
            foreach (var c in sorted) if (start is null || c.At >= start + span) start = c.At;
            if (start is { } s && now < s + span) end = s + span;
            else start = now;
        }
        else if (kind == "monday")
        {
            var monday = local.Date.AddDays(-((int)local.DayOfWeek + 6) % 7);
            start = Local(monday); end = Local(monday.AddDays(7));
        }
        else if (kind == "subscription_day" && day is >= 1 and <= 31)
        {
            DateTime Anchor(int offset) { var month = new DateTime(local.Year, local.Month, 1).AddMonths(offset); return month.AddDays(Math.Min(day.Value, DateTime.DaysInMonth(month.Year, month.Month)) - 1); }
            var current = Local(Anchor(0));
            start = current <= now ? current : Local(Anchor(-1)); end = current <= now ? Local(Anchor(1)) : current;
        }
        var rolling = start is null;
        var used = sorted.Where(x => x.At >= (start ?? now - span) && (end is null || x.At < end)).ToArray();
        if (rolling && used.Length > 0) end = used[0].At + span;
        return new((int)Math.Clamp(Math.Round(used.Sum(x => x.Weight) / limit * 100, MidpointRounding.AwayFromZero), 0, 100), end, now,
            !rolling && end is { } until ? until - start!.Value : span, rolling, true,
            rolling && used.Length > 0 ? used.Count(x => x.At < used[0].At.AddMinutes(1)) : 0);
    }
    public static IReadOnlyList<PlatformStatus> LoadEstimates(AppPaths paths, IEnumerable<InteractionRecord> records, DateTimeOffset now)
    {
        var file = paths.ResolveOwnDataFile("plans.json");
        if (!File.Exists(file)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.EnumerateObject().Where(x => !x.Name.StartsWith('_')).Select(x =>
            {
                var plan = Parse(x.Value, x.Name);
                var rows = records.Where(r => r.ApiHost.Equals(x.Name, StringComparison.OrdinalIgnoreCase) || r.ApiAccount.Equals(x.Name, StringComparison.OrdinalIgnoreCase) || r.Source.StartsWith("API · " + x.Name + " · ", StringComparison.OrdinalIgnoreCase) || r.Source.Equals("API · " + x.Name, StringComparison.OrdinalIgnoreCase));
                return new PlatformStatus(x.Name + " · Plan", plan.Label, false, 0, ProviderDataState.Available,
                    "本机 API 请求估算 · 按配置模型系数计数 · 真值以厂商控制台为准", AlwaysShowDetail: true, ExtraQuotas: plan.Estimate(rows, now));
            }).ToArray();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return [new("Coding Plans", "配置错误", false, 0, ProviderDataState.ReadFailed, "plans.json 无效，请检查请求限额、重置规则与时区", AlwaysShowDetail: true)]; }
    }
}
