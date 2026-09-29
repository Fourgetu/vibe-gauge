using System.Text.Json;

namespace VibeGauge.Core;

public sealed class QuotaAlerts
{
    private readonly string path;
    private readonly Dictionary<string, State> states;
    private bool initialized;
    private DateTimeOffset? lastEvaluation;
    public QuotaAlerts(AppPaths paths)
    {
        path = Path.Combine(paths.LocalDataRoot, "quota-alerts-windows.json");
        try { states = JsonSerializer.Deserialize<Dictionary<string, State>>(File.ReadAllText(path)) ?? []; }
        catch { states = []; }
    }
    public IReadOnlyList<string> Evaluate(IReadOnlyList<PlatformStatus> platforms, ActivityProfile? profile, DateTimeOffset now,
        bool enabled = true, Func<string, ActivityProfile?>? profileFor = null)
    {
        var messages = new List<string>();
        if (!initialized || !enabled || lastEvaluation is not { } last || now - last > TimeSpan.FromSeconds(120) || now < last)
            foreach (var state in states.Values) state.Since = null;
        lastEvaluation = now;
        var observed = new HashSet<string>();
        foreach (var p in enabled ? platforms : [])
            foreach (var (label, w) in new[] { ("5H", p.FiveHour), ("Weekly", p.Weekly),
                (p.SecondaryPoolName + " 5H", p.SecondaryFiveHour), (p.SecondaryPoolName + " Weekly", p.SecondaryWeekly) })
            {
                if (w is null || w.IsEstimate || w.IsRolling || w.Trust(now) != "官方回报" || w.ResetsAt is not { } reset) continue;
                var key = p.Name + "|" + label;
                observed.Add(key);
                if (!states.TryGetValue(key, out var state) || state.Reset <= now && reset > state.Reset)
                    states[key] = state = new() { Reset = reset };
                var forecast = QuotaForecast.Calculate(w, now, profileFor?.Invoke(p.Name) ?? profile);
                if (forecast?.ExhaustAt is not { } exhaust || exhaust >= reset) { state.Since = null; continue; }
                state.Since ??= now;
                if (!initialized || state.Delivered || now - state.Since < TimeSpan.FromMinutes(15)) continue;
                state.Delivered = true;
                messages.Add($"{p.Name} {label} 预计 {Formatting.Countdown(exhaust, now)} 后打满；离重置还有 {Formatting.Countdown(reset, now)}。这是预测，不是已耗尽。");
            }
        foreach (var (key, state) in states) if (!observed.Contains(key)) state.Since = null;
        initialized = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(states));
            File.Move(path + ".tmp", path, true);
        }
        catch (IOException) { }
        return messages;
    }
    public sealed class State
    {
        public DateTimeOffset Reset { get; set; }
        public DateTimeOffset? Since { get; set; }
        public bool Delivered { get; set; }
    }
}
