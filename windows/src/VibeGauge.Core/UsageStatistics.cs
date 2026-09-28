namespace VibeGauge.Core;

public sealed record UsageDay(DateOnly Date, int Calls, long Context, long Output, long CacheRead,
    long CacheWrite = 0, long Thinking = 0);
public sealed record ModelMix(string Source, string Model, int Calls, long Context, long Output,
    long CacheRead = 0, long CacheWrite = 0, long Thinking = 0);
public sealed record UsageModelDay(DateOnly Date, ModelMix Usage);
public sealed record UsageHour(DateOnly Date, int Hour, int Calls);
public sealed record UsagePeriod(DateOnly Start, DateOnly End, int Calls, int ActiveDays,
    long Context, long Output, long CacheRead, long CacheWrite, long Thinking, IReadOnlyList<ModelMix> Models)
{
    public double? CacheHitRate => Context > 0 ? CacheRead * 100d / Context : null;
}
public sealed record UsageStatistics(
    IReadOnlyList<UsageDay> Days, IReadOnlyList<ModelMix> Models, IReadOnlyList<UsageHour> Hours,
    IReadOnlyList<double> ActivityHours, IReadOnlyList<UsageModelDay>? DailyModels = null)
{
    public static UsageStatistics Empty { get; } = new([], [], [], new double[24], []);

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
        TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var zone = timeZone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var rows = records.Where(x => x.Timestamp <= now).ToArray();
        DateOnly Day(InteractionRecord x) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.Timestamp, zone).DateTime);
        var days = rows.GroupBy(Day).Select(g => new UsageDay(g.Key, g.Count(),
            g.Sum(x => x.ContextTokens), g.Sum(x => x.OutputTokens), g.Sum(x => x.CacheReadTokens),
            g.Sum(x => x.CacheWriteTokens), g.Sum(x => x.ThinkingTokens)))
            .OrderBy(x => x.Date).ToArray();
        var dailyModels = rows.GroupBy(x => (Date: Day(x), x.Source, x.Model))
            .Select(g => new UsageModelDay(g.Key.Date, new(g.Key.Source, g.Key.Model, g.Count(),
                g.Sum(x => x.ContextTokens), g.Sum(x => x.OutputTokens), g.Sum(x => x.CacheReadTokens),
                g.Sum(x => x.CacheWriteTokens), g.Sum(x => x.ThinkingTokens))))
            .OrderBy(x => x.Date).ThenByDescending(x => x.Usage.Context).ToArray();
        var models = dailyModels.Where(x => x.Date == today).Select(x => x.Usage).ToArray();
        var hours = rows.GroupBy(x => (Date: Day(x), TimeZoneInfo.ConvertTime(x.Timestamp, zone).Hour))
            .Select(g => new UsageHour(g.Key.Date, g.Key.Hour, g.Count()))
            .OrderBy(x => x.Date).ThenBy(x => x.Hour).ToArray();
        var activity = new double[24];
        foreach (var row in rows.Where(x => now - x.Timestamp < TimeSpan.FromDays(7)))
            activity[TimeZoneInfo.ConvertTime(row.Timestamp, zone).Hour]++;
        return new(days, models, hours, activity, dailyModels);
    }
}
