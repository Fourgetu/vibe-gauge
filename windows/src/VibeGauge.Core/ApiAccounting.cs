using System.Globalization;
using System.Text.Json;

namespace VibeGauge.Core;

public sealed record CostEstimate(decimal Amount, string Currency, string AsOf, int UnpricedModels, bool Configured, int UnknownCalls = 0)
{
    public string Description => !Configured ? "未配置 prices.json · 不估算成本" :
        $"API 等价成本 {Currency} {Amount:N4}" + (UnpricedModels > 0 ? $" · {UnpricedModels} 个模型未定价" : "") +
        (UnknownCalls > 0 ? $" · {UnknownCalls} 次用量未知" : "") + "（非实际账单）";
}

public sealed class PriceTable
{
    public sealed record Rate(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);
    public Dictionary<string, Rate> Rates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Currency { get; private set; } = "USD";
    public string AsOf { get; private set; } = "";
    public string Error { get; private set; } = "";
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
        foreach (var item in doc.RootElement.EnumerateObject())
        {
            if (item.Name == "_currency") { table.Currency = item.Value.GetString() ?? "USD"; continue; }
            if (item.Name == "_asof") { table.AsOf = item.Value.GetString() ?? ""; continue; }
            if (item.Name.StartsWith('_')) continue;
            decimal Read(string key, decimal fallback = 0) => item.Value.TryGetProperty(key, out var value) && value.TryGetDecimal(out var n) && n >= 0 ? n : fallback;
            var input = Read("in");
            var rate = new Rate(input, Read("out"), Read("cache_read", input), Read("cache_write", input));
            if (rate.Input + rate.Output + rate.CacheRead + rate.CacheWrite > 0) table.Rates[item.Name] = rate;
        }
        return table;
    }
    public decimal? Cost(string model, long context, long output, long read, long write)
    {
        var rate = Rates.Where(x => model.StartsWith(x.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Key.Length).Select(x => x.Value).FirstOrDefault();
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
        return new(sum, Currency, AsOf, missing.Count, Rates.Count > 0, unknownCalls);
    }
}

public sealed record ApiRateLimit(string Header, string Value, DateTimeOffset CapturedAt)
{
    public string DisplayValue
    {
        get
        {
            if (!Header.Contains("reset", StringComparison.OrdinalIgnoreCase) && !Header.Equals("retry-after", StringComparison.OrdinalIgnoreCase)) return Value;
            if (DateTimeOffset.TryParse(Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) && Value.Contains(':'))
                return Value + $" → {date.LocalDateTime:MM-dd HH:mm:ss}";
            if (double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0)
            {
                if (number is > 1e12 and < 4.2e12) date = DateTimeOffset.FromUnixTimeMilliseconds((long)number);
                else if (number is > 1e9 and < 4.2e9) date = DateTimeOffset.FromUnixTimeSeconds((long)number);
                else if (Header.Equals("retry-after", StringComparison.OrdinalIgnoreCase) && number <= 86400 * 366) date = CapturedAt.AddSeconds(number);
                else return Value;
                return Value + $" → {date.LocalDateTime:MM-dd HH:mm:ss}";
            }
            return Value;
        }
    }
}
public sealed record ApiQuality(int P50, int P95, int Maximum, int RateLimited, IReadOnlyList<ApiRateLimit> Limits)
{
    public static ApiQuality Build(IEnumerable<InteractionRecord> records)
    {
        var rows = records.ToArray();
        var ms = rows.Where(x => x.LatencyMs > 0).Select(x => x.LatencyMs).Order().ToArray();
        int Percentile(double p) => ms.Length == 0 ? 0 : ms[Math.Clamp((int)Math.Ceiling(ms.Length * p) - 1, 0, ms.Length - 1)];
        var limits = rows.OrderByDescending(x => x.Timestamp).SelectMany(x => (x.RateLimits ?? new Dictionary<string, string>())
                .Select(r => new ApiRateLimit(r.Key, r.Value, x.Timestamp)))
            .DistinctBy(x => x.Header, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(Percentile(.5), Percentile(.95), ms.LastOrDefault(), rows.Count(x => x.Status == 429), limits);
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
        if (parent is not null) foreach (var r in parent.Reset) plan.Reset[r.Key] = r.Value;
        if (root.TryGetProperty("requests", out var limits))
            foreach (var r in limits.EnumerateObject()) if (r.Value.TryGetInt32(out var n) && n > 0) plan.Requests[r.Name] = n;
        if (root.TryGetProperty("reset", out var resets))
            foreach (var r in resets.EnumerateObject()) plan.Reset[r.Name] = r.Value.GetString() ?? "rolling";
        if (root.TryGetProperty("weights", out var weights))
            foreach (var r in weights.EnumerateObject()) if (r.Value.TryGetDouble(out var n) && double.IsFinite(n) && n > 0) plan.Weights[r.Name] = n;
        if (DateOnly.TryParseExact(root.StringOrEmpty("subscribed_on"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) plan.SubscriptionDay = date.Day;
        if (root.StringOrEmpty("timezone") is { Length: > 0 } zone) plan.Zone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        if (root.TryGetProperty("models", out var models))
            foreach (var model in models.EnumerateObject()) plan.Models[model.Name] = Parse(model.Value, model.Name, plan);
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
                var rows = records.Where(r => r.Source.StartsWith("API · " + x.Name + " · ", StringComparison.OrdinalIgnoreCase) || r.Source.Equals("API · " + x.Name, StringComparison.OrdinalIgnoreCase));
                return new PlatformStatus(x.Name + " · Plan", plan.Label, false, 0, ProviderDataState.Available,
                    "本机 API 请求估算 · 按配置模型系数计数 · 真值以厂商控制台为准", AlwaysShowDetail: true, ExtraQuotas: plan.Estimate(rows, now));
            }).ToArray();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return [new("Coding Plans", "配置错误", false, 0, ProviderDataState.ReadFailed, "plans.json 无效，请检查请求限额、重置规则与时区", AlwaysShowDetail: true)]; }
    }
}
