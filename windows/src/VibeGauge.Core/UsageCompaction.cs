using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;

namespace VibeGauge.Core;

public sealed partial class UsageScanner
{
    private bool coldDirty;
    public int HotRecordCount => cache.Files.Values.Sum(x => x.Records.Count);
    public int CompactedCalls => cache.ColdStatistics?.Days.Sum(x => x.Calls) ?? 0;
    private static bool CanCompact(FileState state) => state.Source is not ("Claude" or "API");
    private static int[] BloomBits(InteractionRecord row, int size)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(row.Source + "\0" + row.Id));
        return Enumerable.Range(0, 3).Select(i => (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(i * 4, 4)) % (uint)(size * 8))).ToArray();
    }
    private bool MayMatchCold(InteractionRecord row) => cache.ColdBloom.Length > 0 &&
        BloomBits(row, cache.ColdBloom.Length).All(bit => (cache.ColdBloom[bit / 8] & (1 << (bit % 8))) != 0);

    private void Hydrate(FileState state)
    {
        if (state.PackedRecords.Length == 0) return;
        sourceRecords.Remove(state.Source);
        using var buffer = new MemoryStream(Convert.FromBase64String(state.PackedRecords));
        using var compressed = new GZipStream(buffer, CompressionMode.Decompress);
        var records = JsonSerializer.Deserialize<InteractionRecord[]>(compressed) ?? throw new IOException("Invalid retained history archive");
        foreach (var row in records) state.Records.TryAdd(row.Id, row);
        state.PackedRecords = "";
        coldDirty = true;
    }
    private void CompactHistory(DateTimeOffset now)
    {
        var cutoff = new DateTimeOffset(now.LocalDateTime.Date.AddDays(-90));
        if (!coldDirty && cache.ColdTimeZone == TimeZoneInfo.Local.Id &&
            !cache.Files.Values.Any(s => CanCompact(s) && s.Records.Values.Any(x => x.Timestamp < cutoff))) return;
        sourceRecords.Clear();
        if (!cache.Files.Values.Any(s => CanCompact(s) && (s.PackedRecords.Length > 0 || s.Records.Values.Any(x => x.Timestamp < cutoff))))
        {
            if (coldDirty) { cache.ColdStatistics = null; cache.ColdTotals.Clear(); cache.ColdBloom = []; cacheDirty = true; coldDirty = false; }
            cache.ColdTimeZone = TimeZoneInfo.Local.Id; return;
        }

        // Keep a single pre-migration snapshot. Archives retain exact replay identities;
        // ordinary refreshes use only day/source/model totals and never inflate them.
        var backup = cachePath + ".pre-compaction.bak";
        if (File.Exists(cachePath) && !File.Exists(backup)) File.Copy(cachePath, backup);
        foreach (var state in cache.Files.Values.Where(CanCompact)) Hydrate(state);
        var all = CodexRecords(CachedFiles("Codex"))
            .Concat(PiRecords(CachedFiles(PiDesktopUsage.SourceName)))
            .Concat(MostRecentPerId(CachedFiles(ZCodeUsage.SourceName).SelectMany(x => x.Value.Records.Values)))
            .Concat(MostRecentPerId(CachedFiles(WorkBuddyUsage.SourceName).SelectMany(x => x.Value.Records.Values)))
            .Concat(MostRecentPerId(CachedFiles(DshUsage.SourceName).SelectMany(x => x.Value.Records.Values)));
        var cold = all.Where(x => x.Timestamp < cutoff).ToArray();
        cache.ColdStatistics = UsageStatistics.Build(cold, now);
        cache.ColdTotals = cold.GroupBy(x => x.Source).ToDictionary(x => x.Key, x => SourceSummary(x.Key, x.ToArray(), true, ""));
        cache.ColdBloom = new byte[Math.Clamp(cold.Length * 4, 1024, 1024 * 1024)];
        foreach (var row in cold.Where(x => x.Source != "Codex"))
            foreach (var bit in BloomBits(row, cache.ColdBloom.Length)) cache.ColdBloom[bit / 8] |= (byte)(1 << (bit % 8));
        cache.ColdTimeZone = TimeZoneInfo.Local.Id;
        foreach (var state in cache.Files.Values.Where(CanCompact))
        {
            var old = state.Records.Values.Where(x => x.Timestamp < cutoff).ToArray();
            if (old.Length == 0) continue;
            using var buffer = new MemoryStream();
            using (var compressed = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true)) JsonSerializer.Serialize(compressed, old);
            state.PackedRecords = Convert.ToBase64String(buffer.ToArray());
            foreach (var row in old) state.Records.Remove(row.Id);
        }
        coldDirty = false;
        cacheDirty = true;
    }
    private UsageSourceSummary WithColdTotal(UsageSourceSummary current)
    {
        if (!cache.ColdTotals.TryGetValue(current.Name, out var old)) return current;
        return current with { Turns = current.Turns + old.Turns, ContextTokens = current.ContextTokens + old.ContextTokens,
            CacheReadTokens = current.CacheReadTokens + old.CacheReadTokens, CacheWriteTokens = current.CacheWriteTokens + old.CacheWriteTokens,
            OutputTokens = current.OutputTokens + old.OutputTokens, ThinkingTokens = current.ThinkingTokens + old.ThinkingTokens,
            State = current.State == UsageDataState.ReadFailed ? current.State : UsageDataState.Available,
            Note = current.State == UsageDataState.ReadFailed ? current.Note : "" };
    }
}
