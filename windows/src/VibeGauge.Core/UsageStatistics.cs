using System.Text.Json.Serialization;

namespace VibeGauge.Core;

public sealed record UsageDay(DateOnly Date, int Calls, long Context, long Output, long CacheRead,
    long CacheWrite = 0, long Thinking = 0)
{
    [JsonIgnore] public long TotalTokens => Context + Output;
}
public sealed record ModelMix(string Source, string Model, int Calls, long Context, long Output,
    long CacheRead = 0, long CacheWrite = 0, long Thinking = 0)
{
    [JsonIgnore] public long TotalTokens => Context + Output;
}
public sealed record UsageModelDay(DateOnly Date, ModelMix Usage);
public sealed record UsageHour(DateOnly Date, int Hour, int Calls);
public sealed record UsagePeriod(DateOnly Start, DateOnly End, int Calls, int ActiveDays,
    long Context, long Output, long CacheRead, long CacheWrite, long Thinking, IReadOnlyList<ModelMix> Models)
{
    [JsonIgnore] public long TotalTokens => Context + Output;
    public double? CacheHitRate => Context > 0 ? CacheRead * 100d / Context : null;
}
public sealed record UsageStatistics(
    IReadOnlyList<UsageDay> Days, IReadOnlyList<ModelMix> Models, IReadOnlyList<UsageHour> Hours,
    IReadOnlyList<double> ActivityHours, IReadOnlyList<UsageModelDay>? DailyModels = null,
    IReadOnlyDictionary<string, double[]>? SourceActivity = null, PriceTable? Prices = null)
{
    public static UsageStatistics Empty { get; } = new([], [], [], new double[24], []);

    public UsageStatistics WithHistory(UsageStatistics? history)
    {
        if (history is null || history.Days.Count == 0) return this;
        return this with {
            Days = Days.Concat(history.Days).GroupBy(x => x.Date).Select(g => new UsageDay(g.Key, g.Sum(x => x.Calls), g.Sum(x => x.Context),
                g.Sum(x => x.Output), g.Sum(x => x.CacheRead), g.Sum(x => x.CacheWrite), g.Sum(x => x.Thinking))).OrderBy(x => x.Date).ToArray(),
            DailyModels = (DailyModels ?? []).Concat(history.DailyModels ?? []).GroupBy(x => (x.Date, x.Usage.Source, x.Usage.Model))
                .Select(g => new UsageModelDay(g.Key.Date, new(g.Key.Source, g.Key.Model, g.Sum(x => x.Usage.Calls),
                    g.Sum(x => x.Usage.Context), g.Sum(x => x.Usage.Output), g.Sum(x => x.Usage.CacheRead), g.Sum(x => x.Usage.CacheWrite), g.Sum(x => x.Usage.Thinking))))
                .OrderBy(x => x.Date).ThenByDescending(x => x.Usage.Context).ToArray(),
            Hours = Hours.Concat(history.Hours).GroupBy(x => (x.Date, x.Hour)).Select(g => new UsageHour(g.Key.Date, g.Key.Hour, g.Sum(x => x.Calls)))
                .OrderBy(x => x.Date).ThenBy(x => x.Hour).ToArray()
        };
    }

    public ActivityProfile? ProfileFor(string provider)
    {
        var own = (SourceActivity ?? new Dictionary<string, double[]>()).Where(x => ProviderDetails.Matches(provider, x.Key)).ToArray();
        var api = (SourceActivity ?? new Dictionary<string, double[]>()).Where(x => x.Key.StartsWith("API · ", StringComparison.Ordinal) &&
            x.Key[6..].Split(" · ")[0].Equals(provider, StringComparison.OrdinalIgnoreCase)).ToArray();
        static ActivityProfile? Merge(KeyValuePair<string, double[]>[] rows) => rows.Length == 0 ? null :
            ActivityProfile.From(Enumerable.Range(0, 24).Select(i => rows.Sum(x => x.Value[i])).ToArray());
        return (own.Length > 0 ? Merge(own) : Merge(api)) ?? ActivityProfile.From(ActivityHours);
    }

    public UsagePeriod ForPeriod(DateOnly start, DateOnly end)
    {
        if (start > end) throw new ArgumentException("The start date must not follow the end date.", nameof(start));
        var days = Days.Where(x => x.Date >= start && x.Date <= end).ToArray();
        var models = (DailyModels ?? []).Where(x => x.Date >= start && x.Date <= end)
            .GroupBy(x => (x.Usage.Source, x.Usage.Model))
            .Select(g => new ModelMix(g.Key.Source, g.Key.Model, g.Sum(x => x.Usage.Calls),
                g.Sum(x => x.Usage.Context), g.Sum(x => x.Usage.Output), g.Sum(x => x.Usage.CacheRead),
                g.Sum(x => x.Usage.CacheWrite), g.Sum(x => x.Usage.Thinking)))
            .OrderByDescending(x => x.Context).ThenByDescending(x => x.Output)
            .ThenBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Model, StringComparer.Ordinal).ToArray();
        return new(start, end, days.Sum(x => x.Calls), days.Count(x => x.Calls > 0),
            days.Sum(x => x.Context), days.Sum(x => x.Output), days.Sum(x => x.CacheRead),
            days.Sum(x => x.CacheWrite), days.Sum(x => x.Thinking), models);
    }

    public static UsageStatistics Build(IEnumerable<InteractionRecord> records, DateTimeOffset now,
        TimeZoneInfo? timeZone = null, IEnumerable<InteractionRecord>? apiRecords = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var zone = timeZone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var dayTotals = new Dictionary<DateOnly, Totals>();
        var modelTotals = new Dictionary<(DateOnly Date, string Source, string Model), Totals>();
        var hourTotals = new Dictionary<(DateOnly Date, int Hour), int>();
        var activity = new double[24];
        var sourceActivity = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        void AddActivity(InteractionRecord row)
        {
            if (row.Timestamp > now || now - row.Timestamp >= TimeSpan.FromDays(7)) return;
            if (!sourceActivity.TryGetValue(row.Source, out var hours)) sourceActivity[row.Source] = hours = new double[24];
            hours[TimeZoneInfo.ConvertTime(row.Timestamp, zone).Hour]++;
        }
        foreach (var row in records)
        {
            AddActivity(row);
            if (row.Timestamp > now) continue;
            var local = TimeZoneInfo.ConvertTime(row.Timestamp, zone);
            var day = DateOnly.FromDateTime(local.DateTime);
            if (!dayTotals.TryGetValue(day, out var daily)) dayTotals[day] = daily = new();
            daily.Add(row);
            var key = (day, row.Source, row.Model);
            if (!modelTotals.TryGetValue(key, out var model)) modelTotals[key] = model = new();
            model.Add(row);
            var hourKey = (day, local.Hour);
            hourTotals[hourKey] = hourTotals.GetValueOrDefault(hourKey) + 1;
            if (now - row.Timestamp < TimeSpan.FromDays(7)) activity[local.Hour]++;
        }
        var days = dayTotals.Select(g => new UsageDay(g.Key, g.Value.Calls,
            g.Value.Context, g.Value.Output, g.Value.Read, g.Value.Write, g.Value.Thinking))
            .OrderBy(x => x.Date).ToArray();
        var dailyModels = modelTotals.Select(g => new UsageModelDay(g.Key.Date,
                new(g.Key.Source, g.Key.Model, g.Value.Calls, g.Value.Context, g.Value.Output,
                    g.Value.Read, g.Value.Write, g.Value.Thinking)))
            .OrderBy(x => x.Date).ThenByDescending(x => x.Usage.Context).ToArray();
        var models = dailyModels.Where(x => x.Date == today).Select(x => x.Usage).ToArray();
        var hours = hourTotals.Select(g => new UsageHour(g.Key.Date, g.Key.Hour, g.Value))
            .OrderBy(x => x.Date).ThenBy(x => x.Hour).ToArray();
        foreach (var row in apiRecords ?? []) AddActivity(row);
        return new(days, models, hours, activity, dailyModels, sourceActivity);
    }

    private sealed class Totals
    {
        internal int Calls;
        internal long Context, Output, Read, Write, Thinking;
        internal void Add(InteractionRecord row)
        {
            Calls++;
            checked
            {
                Context += row.ContextTokens; Output += row.OutputTokens; Read += row.CacheReadTokens;
                Write += row.CacheWriteTokens; Thinking += row.ThinkingTokens;
            }
        }
    }
}
