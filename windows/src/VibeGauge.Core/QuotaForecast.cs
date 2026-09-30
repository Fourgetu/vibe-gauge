using System.Text.Json;

namespace VibeGauge.Core;

public sealed record QuotaBurn(double PercentPerHour, int ProjectedPercent, DateTimeOffset? ExhaustAt, string Basis);

public sealed class ActivityProfile
{
    public double[] Share { get; }
    private ActivityProfile(double[] share) => Share = share;
    public static ActivityProfile? From(IReadOnlyList<double> hours)
    {
        if (hours.Count != 24 || hours.Any(x => !double.IsFinite(x) || x < 0) ||
            hours.Sum() < 30 || hours.Count(x => x > 0) < 3) return null;
        var total = hours.Sum();
        return new(hours.Select(x => .9 * x / total + .1 / 24).ToArray());
    }

    // Integrate actual UTC minutes so DST and fractional-hour time zones stay correct.
    public double ActiveDays(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo? zone = null)
    {
        if (to <= from) return 0;
        zone ??= TimeZoneInfo.Local;
        double result = 0;
        for (var at = from; at < to;)
        {
            var next = at.AddSeconds(60 - at.Second);
            if (next > to) next = to;
            result += Share[TimeZoneInfo.ConvertTime(at, zone).Hour] * (next - at).TotalHours;
            at = next;
        }
        return result;
    }

    public DateTimeOffset? Exhaust(DateTimeOffset from, DateTimeOffset limit, double need, double perDay)
    {
        if (perDay <= 0 || ActiveDays(from, limit) * perDay < need) return null;
        var low = from;
        var high = limit;
        for (var i = 0; i < 24; i++)
        {
            var mid = low + (high - low) / 2;
            if (ActiveDays(from, mid) * perDay < need) low = mid; else high = mid;
        }
        return high;
    }
}

public static class QuotaForecast
{
    public static QuotaBurn? Calculate(QuotaWindow w, DateTimeOffset now, ActivityProfile? profile = null)
    {
        if (w.Window <= TimeSpan.Zero || w.Window > TimeSpan.FromDays(366) || w.IsRolling ||
            w.IsEstimate || w.ResetsAt is not { } reset || w.Trust(now) != "官方回报") return null;
        var remaining = reset - now;
        var elapsed = w.Window - remaining;
        var pct = w.EffectivePercent(now);
        if (remaining <= TimeSpan.Zero || elapsed < TimeSpan.Zero || pct >= 100) return null;
        double rate, projected;
        DateTimeOffset? exhaust;
        string basis;
        if (w.Window >= TimeSpan.FromDays(1) && profile is not null)
        {
            var done = profile.ActiveDays(reset - w.Window, now);
            if (w.LastCyclePercent is null && (done < .25 || pct <= 0)) return null;
            var perDay = w.LastCyclePercent is { } last
                ? (pct + last / w.Window.TotalDays) / (done + 1)
                : pct / done;
            projected = pct + perDay * profile.ActiveDays(now, reset);
            rate = perDay / 24;
            exhaust = profile.Exhaust(now, reset, 100 - pct, perDay);
            basis = w.LastCyclePercent is { } previous ? $"作息 · 上周期 {previous}%" : "近 7 天作息";
        }
        else
        {
            if (w.RecentSpanMinutes >= 10)
            {
                rate = Math.Max(0, w.RecentPercentPerHour);
                basis = $"近 {w.RecentSpanMinutes} 分钟";
            }
            else
            {
                if (elapsed < TimeSpan.FromMinutes(15) || pct <= 0) return null;
                rate = pct / elapsed.TotalHours;
                basis = "本窗口均";
            }
            projected = pct + rate * remaining.TotalHours;
            exhaust = projected >= 100 && rate > 0 ? now.AddHours((100 - pct) / rate) : null;
        }
        return new(rate, (int)Math.Clamp(Math.Round(projected), 0, int.MaxValue), exhaust, basis);
    }
}

public sealed class QuotaSampler(AppPaths paths)
{
    private Dictionary<string, Cycle>? cycles;
    public IReadOnlyList<PlatformStatus> Apply(IReadOnlyList<PlatformStatus> platforms, DateTimeOffset now)
    {
        var path = Path.Combine(paths.LocalDataRoot, "quota-samples-windows.json");
        if (cycles is null)
        {
            try { cycles = JsonSerializer.Deserialize<Dictionary<string, Cycle>>(File.ReadAllText(path)); } catch { }
            cycles ??= [];
        }
        QuotaWindow? Fill(QuotaWindow? w, string key)
        {
            if (w?.ResetsAt is not { } reset || w.IsRolling || w.IsEstimate || reset <= now) return w;
            if (!cycles.TryGetValue(key, out var cycle)) cycles[key] = cycle = new() { Reset = reset };
            if (Math.Abs((cycle.Reset - reset).TotalSeconds) > 60)
            {
                var last = cycle.Points.LastOrDefault(x => x.At < cycle.Reset);
                var final = cycle.Reset <= now && last is not null &&
                    cycle.Reset - last.At <= (w.Window < TimeSpan.FromDays(1) ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(12))
                    ? last.Percent : (int?)null;
                cycles[key] = cycle = new() { Reset = reset, Last = final };
            }
            var observed = w.CapturedAt is { } at && at < now ? at : now;
            if (cycle.Points.Count == 0 || observed - cycle.Points[^1].At >= TimeSpan.FromSeconds(55))
                cycle.Points.Add(new(observed, w.UsedPercent));
            cycle.Points.RemoveAll(x => now - x.At > TimeSpan.FromHours(6));
            var latest = cycle.Points.LastOrDefault();
            var oldest = latest is null ? null : cycle.Points.FirstOrDefault(x => latest.At - x.At <= TimeSpan.FromHours(1));
            w = w with { LastCyclePercent = cycle.Last };
            if (latest is null || oldest is null || now - latest.At > TimeSpan.FromHours(1) ||
                latest.At - oldest.At < TimeSpan.FromMinutes(10) || latest.Percent < oldest.Percent) return w;
            return w with
            {
                RecentPercentPerHour = (latest.Percent - oldest.Percent) / (latest.At - oldest.At).TotalHours,
                RecentSpanMinutes = (int)(latest.At - oldest.At).TotalMinutes
            };
        }
        var result = platforms.Select(p => p with
        {
            FiveHour = Fill(p.FiveHour, p.QuotaHistoryKey + "|5h"),
            Weekly = Fill(p.Weekly, p.QuotaHistoryKey + "|w"),
            SecondaryFiveHour = Fill(p.SecondaryFiveHour, p.QuotaHistoryKey + "|" + p.SecondaryPoolName + "|5h"),
            SecondaryWeekly = Fill(p.SecondaryWeekly, p.QuotaHistoryKey + "|" + p.SecondaryPoolName + "|w"),
            Monthly = Fill(p.Monthly, p.QuotaHistoryKey + "|month")
        }).ToArray();
        try
        {
            Directory.CreateDirectory(paths.LocalDataRoot);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(cycles));
            File.Move(path + ".tmp", path, true);
        }
        catch (IOException) { }
        return result;
    }
    public sealed class Cycle
    {
        public DateTimeOffset Reset { get; set; }
        public int? Last { get; set; }
        public List<Point> Points { get; set; } = [];
    }
    public sealed record Point(DateTimeOffset At, int Percent);
}
