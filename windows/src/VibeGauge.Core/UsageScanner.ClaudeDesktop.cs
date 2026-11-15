using System.Text.Json;

namespace VibeGauge.Core;

public sealed partial class UsageScanner
{
    private ClaudeDesktopUsageLocation desktopLocation = new(false);

    private bool ProcessDesktopLedger(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 20 * 1024 * 1024) throw new IOException("Desktop usage file exceeds supported size");
        if (!cache.Files.TryGetValue(path, out var state) || state.UsageScope != desktopLocation.Scope)
            state = new FileState { Source = ClaudeDesktop.Name, UsageScope = desktopLocation.Scope };
        if (state.Size == info.Length && state.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks && state.Head.Length > 0) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var records = new List<InteractionRecord>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        // Replay whole bounded daily chunks. Stable numeric identities survive truncation,
        // rotation, copies and restarts; a partial last line is retried on the next scan.
        var offset = JsonLineReader.Read(stream, 0, stream.Length, (line, _) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (ClaudeDesktopUsage.Parse(doc.RootElement, desktopLocation.Scope) is not { } record) return;
                var occurrence = occurrences.GetValueOrDefault(record.Id);
                occurrences[record.Id] = occurrence + 1;
                records.Add(record with { Id = record.Id + ":" + occurrence });
            }
            catch (JsonException) { }
        }, 256 * 1024);
        foreach (var record in records) state.Records[record.Id] = record;
        state.Size = info.Length;
        state.LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
        state.Offset = offset;
        state.Head = "desktop-ledger-v1";
        cache.Files[path] = state;
        return true;
    }

    private InteractionRecord[] DesktopRecords(IEnumerable<InteractionRecord> claude)
    {
        if (desktopLocation.Scope.Length == 0) return [];
        var cliSessions = claude.Where(x => x.SessionId.Length > 0).Select(x => x.SessionId).ToHashSet(StringComparer.Ordinal);
        return MostRecentPerId(CachedFiles(ClaudeDesktop.Name).Where(x => x.Value.UsageScope == desktopLocation.Scope)
            .SelectMany(x => x.Value.Records.Values))
            // A Code session may also be visible in the CLI log. Prefer the detailed
            // CLI usage for that session rather than adding its aggregate ledger twice.
            .Where(x => x.UsageSurface != "code" || x.SessionId.Length == 0 || !cliSessions.Contains(x.SessionId)).ToArray();
    }
}
