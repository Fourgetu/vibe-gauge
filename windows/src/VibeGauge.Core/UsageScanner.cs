using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeGauge.Core;

public sealed class UsageScanner
{
    private static readonly string[] TokenKeys =
    [
        "input_tokens", "cached_input_tokens", "cache_write_input_tokens",
        "output_tokens", "reasoning_output_tokens"
    ];

    private readonly AppPaths paths;
    private readonly string cachePath;
    private readonly object sync = new();
    private ScannerCache cache = new();
    private bool loaded;
    private bool cacheDirty;

    public UsageScanner(AppPaths paths)
    {
        this.paths = paths;
        cachePath = Path.Combine(paths.LocalDataRoot, "usage-incremental.json");
    }

    public UsageScannerDiagnostics LastDiagnostics { get; private set; } = new(0, 0, 0);

    public UsageSummary ScanToday() => Scan().Cli;

    public UsageScanResult Scan()
    {
        lock (sync)
        {
            LoadCache();
            var discovered = DiscoverFiles();
            var filesRead = 0;
            long bytesRead = 0;
            var errors = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in discovered)
            {
                try
                {
                    if (ProcessFile(item.Path, item.Source, out var read))
                    {
                        filesRead++;
                        bytesRead += read;
                        cacheDirty = true;
                    }
                }
                catch
                {
                    errors.Add("部分日志无法读取，保留上次结果并等待重试");
                }
            }

            if (cacheDirty) cacheDirty = !SaveCache();
            LastDiagnostics = new(discovered.Count, filesRead, bytesRead);
            return Summarize(discovered, errors);
        }
    }

    private List<DiscoveredFile> DiscoverFiles()
    {
        var result = new List<DiscoveredFile>();
        AddFiles(result, Path.Combine(paths.ClaudeRoot, "projects"), "Claude");
        AddFiles(result, Path.Combine(paths.CodexRoot, "sessions"), "Codex");
        AddFiles(result, paths.PiDesktopSessions, PiDesktopUsage.SourceName);
        var api = paths.ResolveOwnDataFile("api-calls.jsonl");
        if (File.Exists(api)) result.Add(new(api, "API"));
        return result.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddFiles(List<DiscoveredFile> result, string root, string source)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            result.AddRange(Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new DiscoveredFile(path, source)));
        }
        catch { }
    }

    private bool ProcessFile(string path, string source, out long bytesRead)
    {
        bytesRead = 0;
        var info = new FileInfo(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var headBytes = new byte[Math.Min(256, (int)Math.Min(stream.Length, int.MaxValue))];
        var headLength = stream.Read(headBytes, 0, headBytes.Length);
        var head = Fingerprint(headBytes.AsSpan(0, headLength));

        if (!cache.Files.TryGetValue(path, out var state)) state = new FileState { Source = source };
        var oldHeadLength = (int)Math.Min(256, state.Size);
        var comparableHead = Fingerprint(headBytes.AsSpan(0, Math.Min(headLength, oldHeadLength)));
        var tailChanged = false;
        if (state.Tail.Length > 0 && state.Offset >= 64 && stream.Length >= state.Offset)
        {
            stream.Position = state.Offset - 64;
            var tail = new byte[64];
            stream.ReadExactly(tail);
            tailChanged = Fingerprint(tail) != state.Tail;
        }
        var rewritten = tailChanged || state.Source != source || state.Offset > stream.Length || stream.Length < state.Size ||
                        state.Head.Length > 0 && comparableHead != state.HeadPrefix ||
                        stream.Length == state.Size && info.LastWriteTimeUtc.Ticks != state.LastWriteUtcTicks;
        if (rewritten) state = new FileState { Source = source };
        if (state.Size == stream.Length && state.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks && state.Head == head)
            return false;

        // Migrate the old cursor, which included an incomplete tail, without losing records.
        if (state.Pending.Length > 0)
        {
            state.Offset -= Convert.FromBase64String(state.Pending).Length;
            state.Pending = "";
        }
        bytesRead = stream.Length - state.Offset;
        state.Offset = JsonLineReader.Read(stream, state.Offset, stream.Length, (line, _) => Consume(line, state),
            JsonLineReader.MaxLineBytes, bytes => IsRelevantLine(bytes, source));
        if (state.Offset >= 64)
        {
            stream.Position = state.Offset - 64;
            var tail = new byte[64];
            stream.ReadExactly(tail);
            state.Tail = Fingerprint(tail);
        }
        state.Size = stream.Length;
        state.LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
        state.Head = head;
        state.HeadPrefix = Fingerprint(headBytes.AsSpan(0, Math.Min(headLength, (int)Math.Min(256, state.Size))));
        cache.Files[path] = state;
        return true;
    }

    private static bool IsRelevantLine(ReadOnlyMemory<byte> line, string source) => source switch
    {
        "Claude" or PiDesktopUsage.SourceName => line.Span.IndexOf("\"usage\""u8) >= 0,
        "Codex" => line.Span.IndexOf("\"token_count\""u8) >= 0 ||
            line.Span.IndexOf("\"turn_context\""u8) >= 0 || line.Span.IndexOf("\"session_meta\""u8) >= 0,
        _ => true
    };

    private static void Consume(string line, FileState state)
    {
        if (state.Source == "Claude" && !line.Contains("\"usage\"", StringComparison.Ordinal)) return;
        if (state.Source == PiDesktopUsage.SourceName && !line.Contains("\"usage\"", StringComparison.Ordinal)) return;
        if (state.Source == "Codex" &&
            !line.Contains("\"token_count\"", StringComparison.Ordinal) &&
            !line.Contains("\"turn_context\"", StringComparison.Ordinal) &&
            !line.Contains("\"session_meta\"", StringComparison.Ordinal)) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (state.Source == "Claude") ConsumeClaude(document.RootElement, state);
            else if (state.Source == "Codex") ConsumeCodex(document.RootElement, state);
            else if (state.Source == PiDesktopUsage.SourceName)
            {
                if (PiDesktopUsage.Parse(document.RootElement) is { } record) state.Records[record.Id] = record;
            }
            else ConsumeApi(document.RootElement, state);
        }
        catch (JsonException) { state.Skipped++; }
    }

    private static void ConsumeClaude(JsonElement root, FileState state)
    {
        if (root.StringOrEmpty("type") != "assistant" ||
            !root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("usage", out var usage)) return;
        var model = message.StringOrEmpty("model");
        if (model.Contains("synthetic", StringComparison.OrdinalIgnoreCase)) return;
        var id = root.StringOrEmpty("requestId");
        if (id.Length == 0) id = message.StringOrEmpty("id");
        if (id.Length == 0) id = root.StringOrEmpty("uuid");
        var timestamp = Formatting.ParseDate(root.StringOrEmpty("timestamp"));
        if (id.Length == 0 || timestamp is null || !TryNonNegative(usage, "input_tokens", out var input) ||
            !TryNonNegative(usage, "output_tokens", out var output))
        {
            state.Skipped++;
            return;
        }
        var read = NonNegative(usage, "cache_read_input_tokens");
        var write = NonNegative(usage, "cache_creation_input_tokens");
        var think = usage.TryGetProperty("output_tokens_details", out var details)
            ? NonNegative(details, "thinking_tokens")
            : 0;
        state.Records[id] = new(
            id, "Claude", model.Length == 0 ? "?" : model, timestamp.Value,
            input + read + write, read, write, output, think);
    }

    private static void ConsumeCodex(JsonElement root, FileState state)
    {
        if (!root.TryGetProperty("payload", out var payload)) return;
        var type = root.StringOrEmpty("type");
        if (type is "turn_context" or "session_meta")
        {
            var contextModel = payload.StringOrEmpty("model");
            if (contextModel.Length > 0) state.Model = contextModel;
            return;
        }
        if (type != "event_msg" || payload.StringOrEmpty("type") != "token_count" ||
            !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return;

        var total = ReadTokenFields(info, "total_token_usage");
        var last = ReadTokenFields(info, "last_token_usage");
        var previous = state.PreviousTotal;
        if (total is not null) state.PreviousTotal = total;
        if (total is not null && previous is not null && TokenKeys.All(key => total[key] == previous.GetValueOrDefault(key))) return;

        Dictionary<string, long>? usage = last;
        if (usage is null && total is not null && previous is not null)
        {
            if (TokenKeys.Any(key => total[key] < previous.GetValueOrDefault(key))) return;
            usage = TokenKeys.ToDictionary(key => key, key => total[key] - previous.GetValueOrDefault(key));
        }
        if (usage is null || usage["input_tokens"] + usage["output_tokens"] <= 0) return;
        var timestamp = Formatting.ParseDate(root.StringOrEmpty("timestamp"));
        if (timestamp is null) { state.Skipped++; return; }
        var model = payload.StringOrEmpty("model");
        if (model.Length == 0) model = state.Model;
        var id = (state.Sequence++).ToString();
        state.Records[id] = new(
            id, "Codex", model.Length == 0 ? "Codex" : model, timestamp.Value,
            usage["input_tokens"], usage["cached_input_tokens"], usage["cache_write_input_tokens"],
            usage["output_tokens"], usage["reasoning_output_tokens"]);
    }

    private static void ConsumeApi(JsonElement root, FileState state)
    {
        DateTimeOffset? timestamp = Formatting.ParseDate(root.StringOrEmpty("ts"));
        if (timestamp is null && root.DoubleOrNull("epoch") is { } epoch && epoch > 0)
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000));
        if (timestamp is null) { state.Skipped++; return; }
        var host = root.StringOrEmpty("host");
        var provider = root.StringOrEmpty("provider");
        if (provider.Length == 0) provider = host;
        if (provider.Length == 0) provider = "未知";
        var key = root.StringOrEmpty("key");
        if (key.Length > 0) provider += " · " + key;
        var model = root.StringOrEmpty("model");
        var id = (state.Sequence++).ToString();
        state.Records[id] = new(
            id, "API · " + provider, model.Length == 0 ? "?" : model, timestamp.Value,
            NonNegative(root, "ctx"), NonNegative(root, "cache_read"), NonNegative(root, "cache_write"),
            NonNegative(root, "out"), NonNegative(root, "think"),
            (int)NonNegative(root, "status"), (int)NonNegative(root, "ms"));
    }

    private UsageScanResult Summarize(IReadOnlyList<DiscoveredFile> discovered, IReadOnlyCollection<string> errors)
    {
        var start = new DateTimeOffset(DateTime.Today);
        var activePaths = discovered.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claudeFiles = cache.Files.Where(x => x.Value.Source == "Claude" && activePaths.Contains(x.Key)).ToArray();
        var claude = new Dictionary<string, InteractionRecord>(StringComparer.Ordinal);
        foreach (var file in claudeFiles.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var record in file.Value.Records.Values)
            {
                if (!claude.TryGetValue(record.Id, out var old) || old.Timestamp < record.Timestamp)
                    claude[record.Id] = record;
            }
        }
        var codexFiles = cache.Files.Where(x => x.Value.Source == "Codex" && activePaths.Contains(x.Key)).ToArray();
        var codex = codexFiles.SelectMany(file => file.Value.Records.Values).ToArray();
        var piFiles = cache.Files.Where(x => x.Value.Source == PiDesktopUsage.SourceName && activePaths.Contains(x.Key)).ToArray();
        var pi = PiRecords(piFiles).ToArray();
        var apiFiles = cache.Files.Where(x => x.Value.Source == "API" && activePaths.Contains(x.Key)).ToArray();
        var api = apiFiles.SelectMany(file => file.Value.Records.Values).ToArray();

        var now = DateTimeOffset.Now;
        var todayClaude = claude.Values.Where(x => x.Timestamp >= start && x.Timestamp <= now).ToArray();
        var todayCodex = codex.Where(x => x.Timestamp >= start && x.Timestamp <= now).ToArray();
        var todayPi = pi.Where(x => x.Timestamp >= start && x.Timestamp <= now).ToArray();
        var cliRecords = todayClaude.Concat(todayCodex).Concat(todayPi).ToArray();
        var sourceRows = new[]
        {
            SourceSummary("Claude Code", todayClaude, claudeFiles.Length > 0, "本地无今日 token 统计"),
            SourceSummary("Codex", todayCodex, codexFiles.Length > 0, "本地无今日 token 统计"),
            SourceSummary(PiDesktopUsage.SourceName, todayPi, piFiles.Length > 0 || Directory.Exists(paths.PiDesktopRoot), "本地无今日 token 统计"),
            UnsupportedSource("Gemini", Directory.Exists(paths.GeminiRoot))
        };
        var error = string.Join("；", errors);
        var cli = new UsageSummary(
            cliRecords.Length,
            cliRecords.Sum(x => x.ContextTokens),
            cliRecords.Sum(x => x.CacheReadTokens),
            cliRecords.Sum(x => x.CacheWriteTokens),
            cliRecords.Sum(x => x.OutputTokens),
            cliRecords.Sum(x => x.ThinkingTokens),
            sourceRows,
            cliRecords.OrderByDescending(x => x.Timestamp).Take(3).Select(x => QualifyRecentId(x, codexFiles)).ToArray(),
            error);

        api = api.Where(x => x.Timestamp <= now).ToArray();
        var todayApi = api.Where(x => x.Timestamp >= start).ToArray();
        var ranges = new[]
        {
            BuildApiRange("today", "今日", todayApi),
            BuildApiRange("7d", "7D", api.Where(x => x.Timestamp >= DateTimeOffset.Now.AddDays(-7)).ToArray()),
            BuildApiRange("30d", "30D", api.Where(x => x.Timestamp >= DateTimeOffset.Now.AddDays(-30)).ToArray()),
            BuildApiRange("all", "All", api)
        };
        var providerRows = ranges[0].Providers;
        var apiState = errors.Count > 0 && apiFiles.Length > 0 ? UsageDataState.ReadFailed :
            apiFiles.Length == 0 ? UsageDataState.NotDetected :
            todayApi.Length == 0 ? UsageDataState.NoLocalStats : UsageDataState.Available;
        var apiSummary = new ApiUsageSummary(
            apiState, todayApi.Length, todayApi.Sum(x => x.ContextTokens), todayApi.Sum(x => x.OutputTokens),
            todayApi.Sum(x => x.ThinkingTokens), providerRows,
            apiState switch
            {
                UsageDataState.Available => "来自本地 API 调用日志",
                UsageDataState.NoLocalStats => "今日暂无 API 调用",
                UsageDataState.ReadFailed => "部分 API 日志读取失败",
                _ => "未检测到 API 日志"
            },
            ranges,
            api.OrderByDescending(x => x.Timestamp).Take(8)
                .Select(x => new ApiRecentCall(
                    x.Source["API · ".Length..],
                    x.Model,
                    x.Timestamp,
                    x.TotalTokens,
                    x.Status,
                    x.LatencyMs))
                .ToArray());
        var archivedClaude = MostRecentPerId(cache.Files.Where(x => x.Value.Source == "Claude")
            .SelectMany(x => x.Value.Records.Values));
        var archivedCodex = cache.Files.Where(x => x.Value.Source == "Codex").SelectMany(x => x.Value.Records.Values);
        var archivedPi = PiRecords(cache.Files.Where(x => x.Value.Source == PiDesktopUsage.SourceName))
            .Where(x => x.Timestamp <= now).ToArray();
        var piTotal = SourceSummary(PiDesktopUsage.SourceName, archivedPi,
            piFiles.Length > 0 || Directory.Exists(paths.PiDesktopRoot), "本地无 token 统计");
        return new(cli, apiSummary, UsageStatistics.Build(archivedClaude.Concat(archivedCodex).Concat(archivedPi), now), piTotal);
    }

    private static IEnumerable<InteractionRecord> PiRecords(IEnumerable<KeyValuePair<string, FileState>> files) =>
        MostRecentPerId(files.SelectMany(x => x.Value.Records.Values));

    private static IEnumerable<InteractionRecord> MostRecentPerId(IEnumerable<InteractionRecord> records)
    {
        var latest = new Dictionary<string, InteractionRecord>(StringComparer.Ordinal);
        foreach (var record in records)
            if (!latest.TryGetValue(record.Id, out var old) || old.Timestamp < record.Timestamp)
                latest[record.Id] = record;
        return latest.Values;
    }

    private static InteractionRecord QualifyRecentId(InteractionRecord record, IReadOnlyList<KeyValuePair<string, FileState>> codexFiles)
    {
        if (record.Source != "Codex") return record;
        // Only the three displayed turns need a file-qualified ID, not every historical row.
        foreach (var file in codexFiles)
            if (file.Value.Records.TryGetValue(record.Id, out var candidate) && ReferenceEquals(candidate, record))
                return record with { Id = file.Key + ":" + record.Id };
        return record;
    }

    private static ApiRangeSummary BuildApiRange(string key, string label, IReadOnlyList<InteractionRecord> records)
    {
        var providers = records.GroupBy(x => x.Source["API · ".Length..], StringComparer.OrdinalIgnoreCase)
            .Select(group => new ApiProviderSummary(
                group.Key,
                group.Count(),
                group.Sum(x => x.ContextTokens),
                group.Sum(x => x.CacheReadTokens),
                group.Sum(x => x.CacheWriteTokens),
                group.Sum(x => x.OutputTokens),
                group.Sum(x => x.ThinkingTokens),
                group.Count(x => x.Status >= 400 || x.Status == 0),
                group.Count() == 0 ? 0 : (int)Math.Round(group.Average(x => x.LatencyMs))))
            .OrderByDescending(x => x.Calls)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var models = records.GroupBy(
                x => (Provider: x.Source["API · ".Length..], Model: x.Model),
                new ProviderModelComparer())
            .Select(group => new ApiModelSummary(
                group.Key.Provider,
                group.Key.Model,
                group.Count(),
                group.Sum(x => x.ContextTokens),
                group.Sum(x => x.CacheReadTokens),
                group.Sum(x => x.CacheWriteTokens),
                group.Sum(x => x.OutputTokens),
                group.Sum(x => x.ThinkingTokens),
                group.Count(x => x.Status >= 400 || x.Status == 0),
                group.Count() == 0 ? 0 : (int)Math.Round(group.Average(x => x.LatencyMs))))
            .OrderByDescending(x => x.Calls)
            .ThenBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(
            key,
            label,
            records.Count,
            records.Sum(x => x.ContextTokens),
            records.Sum(x => x.CacheReadTokens),
            records.Sum(x => x.CacheWriteTokens),
            records.Sum(x => x.OutputTokens),
            records.Sum(x => x.ThinkingTokens),
            records.Count(x => x.Status >= 400 || x.Status == 0),
            records.Count == 0 ? 0 : (int)Math.Round(records.Average(x => x.LatencyMs)),
            providers,
            models);
    }

    private static UsageSourceSummary SourceSummary(string name, InteractionRecord[] records, bool detected, string emptyNote)
    {
        var state = records.Length > 0 ? UsageDataState.Available :
            detected ? UsageDataState.NoLocalStats : UsageDataState.NotDetected;
        return new(
            name, state, records.Length, records.Sum(x => x.ContextTokens), records.Sum(x => x.CacheReadTokens),
            records.Sum(x => x.CacheWriteTokens), records.Sum(x => x.OutputTokens),
            records.Sum(x => x.ThinkingTokens),
            state == UsageDataState.Available ? "" : detected ? emptyNote : "未检测到");
    }

    private static UsageSourceSummary UnsupportedSource(string name, bool detected) =>
        new(name, detected ? UsageDataState.NoLocalStats : UsageDataState.NotDetected, 0, 0, 0, 0, 0, 0,
            detected ? "本地无 token 统计" : "未检测到");

    private static Dictionary<string, long>? ReadTokenFields(JsonElement info, string property)
    {
        if (!info.TryGetProperty(property, out var fields) || fields.ValueKind != JsonValueKind.Object ||
            !TryNonNegative(fields, "input_tokens", out _) || !TryNonNegative(fields, "output_tokens", out _)) return null;
        return TokenKeys.ToDictionary(key => key, key => NonNegative(fields, key));
    }

    private static bool TryNonNegative(JsonElement root, string property, out long value)
    {
        value = 0;
        if (!root.TryGetProperty(property, out var item) || !item.TryGetInt64(out value) || value < 0)
        {
            value = 0;
            return false;
        }
        return true;
    }

    private static long NonNegative(JsonElement root, string property) =>
        root.TryGetProperty(property, out var item) && item.TryGetInt64(out var value) && value >= 0 ? value : 0;

    private static string Fingerprint(ReadOnlySpan<byte> data) =>
        data.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(data));

    private void LoadCache()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (File.Exists(cachePath))
            {
                using var input = File.OpenRead(cachePath);
                cache = JsonSerializer.Deserialize<ScannerCache>(input) ?? new();
            }
            if (cache.Version != ScannerCache.CurrentVersion) cache = new();
        }
        catch { cache = new(); }
    }

    private bool SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temporary = cachePath + ".tmp";
            using (var output = File.Create(temporary)) JsonSerializer.Serialize(output, cache);
            File.Move(temporary, cachePath, true);
            return true;
        }
        catch { return false; }
    }

    private sealed record DiscoveredFile(string Path, string Source);

    private sealed class ScannerCache
    {
        public const int CurrentVersion = 1;
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FileState
    {
        public string Source { get; set; } = "";
        public string Model { get; set; } = "?";
        public long Size { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public long Offset { get; set; }
        public string Head { get; set; } = "";
        public string HeadPrefix { get; set; } = "";
        public string Pending { get; set; } = "";
        public string Tail { get; set; } = "";
        public int Sequence { get; set; }
        public int Skipped { get; set; }
        public Dictionary<string, long>? PreviousTotal { get; set; }
        public Dictionary<string, InteractionRecord> Records { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ProviderModelComparer : IEqualityComparer<(string Provider, string Model)>
    {
        public bool Equals((string Provider, string Model) x, (string Provider, string Model) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Provider, y.Provider) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Model, y.Model);

        public int GetHashCode((string Provider, string Model) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Provider),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Model));
    }
}
