using System.Text.Json;

namespace VibeGauge.Core;

public sealed class AttentionPolicy
{
    private readonly Dictionary<string, DateTimeOffset> delivered;
    private readonly string file;
    public AttentionPolicy(AppPaths paths)
    {
        file = Path.Combine(paths.LocalDataRoot, "attention-state.json");
        try { delivered = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(file)) ?? []; }
        catch { delivered = []; }
    }
    public IReadOnlyList<string> Evaluate(DashboardSnapshot snapshot, FeaturePreferences options)
    {
        var now = snapshot.CapturedAt;
        var messages = new List<string>();
        void Emit(string id, string text, TimeSpan cooldown)
        {
            if (delivered.TryGetValue(id, out var at) && now - at < cooldown) return;
            delivered[id] = now; messages.Add(text);
        }
        if (options.PendingNotifications)
            foreach (var p in snapshot.Sessions?.Pending ?? [])
                if (now - p.Since >= TimeSpan.FromMinutes(1) && now - p.Since < TimeSpan.FromDays(1))
                    Emit("pending:" + p.Id + ":" + p.Since.ToUnixTimeSeconds(), $"{p.Tool} 等待审批 / 输入超过 1 分钟", TimeSpan.FromDays(1));
        if (options.QuotaNotifications)
            foreach (var p in snapshot.Platforms)
                foreach (var quota in ProviderDetails.Windows(p).Concat(p.ExtraQuotas ?? []))
                    if (!quota.Window.IsEstimate && quota.Window.Trust(now) == "官方回报" && quota.Window.EffectivePercent(now) >= Math.Clamp(options.QuotaThreshold, 50, 100))
                        Emit($"quota:{p.QuotaHistoryKey}:{quota.Label}:{quota.Window.ResetsAt?.ToUnixTimeSeconds()}", $"{p.Name} {quota.Label} 已用 {quota.Window.EffectivePercent(now)}%", TimeSpan.FromDays(32));
        if (options.MemoryNotifications && snapshot.System.AvailableMemoryPercent <= Math.Clamp(options.MemoryFreeThreshold, 1, 50))
            Emit("memory", $"可用内存剩余 {snapshot.System.AvailableMemoryPercent}%", TimeSpan.FromMinutes(30));
        if (options.DiskNotifications && snapshot.System.DiskFreeGb <= Math.Clamp(options.DiskFreeGbThreshold, 1, 500))
            Emit("disk", $"系统盘可用空间 {snapshot.System.DiskFreeGb:0.0} GB", TimeSpan.FromHours(6));
        if (options.EgressNotifications && snapshot.Diagnostics is { } diagnostics)
            foreach (var change in diagnostics.Changes) Emit("egress:" + change, change, TimeSpan.FromMinutes(30));
        foreach (var id in delivered.Where(x => now - x.Value > TimeSpan.FromDays(40)).Select(x => x.Key).ToArray()) delivered.Remove(id);
        if (messages.Count > 0)
            try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(delivered)); File.Move(file + ".tmp", file, true); }
            catch (IOException) { }
        return messages;
    }
}

public sealed class AutoReapPolicy
{
    private readonly Dictionary<string, (DateTimeOffset First, int Observations)> seen = [];
    private DateTimeOffset? lastScan, lastAction;
    private DateTimeOffset nextDue;
    private bool armed;
    public IReadOnlyList<OrphanProcess> Evaluate(IReadOnlyList<OrphanProcess> candidates, bool enabled, bool memoryPressure, DateTimeOffset now)
    {
        if (!enabled) { seen.Clear(); lastScan = null; armed = false; return []; }
        var resumed = lastScan is { } last && now - last > TimeSpan.FromMinutes(2);
        if (resumed || lastScan is { } previous && now < previous) seen.Clear();
        if (lastScan is null || resumed || now >= nextDue || memoryPressure) armed = true;
        lastScan = now;
        static string Key(OrphanProcess p) => $"{p.ProcessId}|{p.StartedAt?.UtcTicks}|{p.CommandFingerprint}";
        var current = candidates.Where(x => x.StartedAt is not null).ToDictionary(Key);
        foreach (var key in seen.Keys.Where(x => !current.ContainsKey(x)).ToArray()) seen.Remove(key);
        foreach (var (key, _) in current)
        {
            if (!seen.TryGetValue(key, out var state)) seen[key] = (now, 1);
            else seen[key] = (state.First, state.Observations + 1);
        }
        if (!armed || lastAction is { } action && now - action < TimeSpan.FromMinutes(5)) return [];
        var eligible = current.Where(x => seen[x.Key].Observations >= 2 && now - seen[x.Key].First >= TimeSpan.FromSeconds(120)).Select(x => x.Value).ToArray();
        if (eligible.Length == 0) return [];
        lastAction = now; nextDue = now.AddMinutes(30); armed = false;
        foreach (var item in eligible) seen.Remove(Key(item));
        return eligible;
    }
}
