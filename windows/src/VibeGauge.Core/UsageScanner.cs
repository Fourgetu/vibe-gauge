using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeGauge.Core;

public sealed partial class UsageScanner
{
    public const string CacheFileName = "usage-ledger-v2.json";
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
    private readonly Dictionary<string, InteractionRecord[]> sourceRecords = new(StringComparer.Ordinal);

    public UsageScanner(AppPaths paths)
    {
        this.paths = paths;
        cachePath = Path.Combine(paths.LocalDataRoot, CacheFileName);
    }

    public UsageScannerDiagnostics LastDiagnostics { get; private set; } = new(0, 0, 0);
    public bool HistoryDurable => loaded && !cacheDirty;

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
                    long read = 0;
                    var changed = item.Source switch
                    {
                        ZCodeUsage.SourceName => ProcessZCode(item.Path),
                        DshUsage.SourceName => ProcessDsh(item.Path),
                        _ => ProcessFile(item.Path, item.Source, out read)
                    };
                    if (changed)
                    {
                        sourceRecords.Remove(item.Source);
                        if (cache.Files.TryGetValue(item.Path, out var updated) && CanCompact(updated) && updated.Source != "Codex" &&
                            updated.Records.Values.Any(MayMatchCold)) coldDirty = true;
                        filesRead++;
                        bytesRead += read;
                        cacheDirty = true;
                    }
                }
                catch
                {
                    sourceRecords.Remove(item.Source);
                    errors.Add(item.Source + " 部分记录无法读取，保留上次结果并等待重试");
                }
            }

            CompactHistory(DateTimeOffset.Now);
            if (cacheDirty) cacheDirty = !SaveCache();
            LastDiagnostics = new(discovered.Count, filesRead, bytesRead);
            return Summarize(errors);
        }
    }

    private List<DiscoveredFile> DiscoverFiles()
    {
        var result = new List<DiscoveredFile>();
        AddFiles(result, Path.Combine(paths.ClaudeRoot, "projects"), "Claude");
        AddFiles(result, Path.Combine(paths.CodexRoot, "sessions"), "Codex");
        AddFiles(result, Path.Combine(paths.CodexRoot, "archived_sessions"), "Codex");
        AddFiles(result, paths.PiDesktopSessions, PiDesktopUsage.SourceName);
        if (File.Exists(paths.ZCodeDatabase)) result.Add(new(paths.ZCodeDatabase, ZCodeUsage.SourceName));
        AddFiles(result, Path.Combine(paths.WorkBuddyRoot, "projects"), WorkBuddyUsage.SourceName);
        AddFiles(result, Path.Combine(paths.WorkBuddyAiRoot, "projects"), WorkBuddyUsage.SourceName);
        if (Directory.Exists(paths.DshSessions))
        {
            try
            {
                result.AddRange(Directory.EnumerateFiles(paths.DshSessions, "session.v*.jsonl*", SearchOption.AllDirectories)
                    .Where(x => x.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase))
                    .Select(x => new DiscoveredFile(x, DshUsage.SourceName)));
            }
            catch { }
        }
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

    private bool ProcessZCode(string path)
    {
        var signature = ZCodeUsage.Signature(path);
        if (!cache.Files.TryGetValue(path, out var state)) state = new FileState { Source = ZCodeUsage.SourceName };
        if (state.Head == signature) return false;
        Hydrate(state);
        // Materialize a successful read before touching retained history. Database/WAL
        // deletion or temporary read failures must not erase already observed requests.
        var records = ZCodeUsage.Read(path);
        foreach (var record in records) state.Records[record.Id] = record;
        state.Head = ZCodeUsage.Signature(path) == signature ? signature : "";
        cache.Files[path] = state;
        return true;
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
                        stream.Length == state.Size && info.LastWriteTimeUtc.Ticks != state.LastWriteUtcTicks ||
                        source == "Codex" && !state.SessionIdentityRead || source == "API" && state.ApiMetadataVersion < 2 ||
                        source is "Claude" or "Codex" && state.ProjectMetadataVersion < 1;
        if (rewritten)
        {
            Hydrate(state);
            // A deleted or rewritten conversation does not undo recorded consumption.
            var retained = state.Source == source ? state : null;
            state = new FileState { Source = source, SessionId = retained?.SessionId ?? "" };
            if (retained is not null)
            {
                state.Records = retained.Records;
                state.Sequence = retained.Sequence;
                if (source is "Codex" or "API")
                    state.ReplayRemaining = retained.Records.Values.GroupBy(x => ReplayFingerprint(x, source))
                        .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            }
        }
        if (state.Size == stream.Length && state.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks && state.Head == head)
            return false;
        Hydrate(state);

        // Migrate the old cursor, which included an incomplete tail, without losing records.
        if (state.Pending.Length > 0)
        {
            state.Offset -= Convert.FromBase64String(state.Pending).Length;
            state.Pending = "";
        }
        bytesRead = stream.Length - state.Offset;
        state.Offset = JsonLineReader.Read(stream, state.Offset, stream.Length, (line, _) => Consume(line, state),
            JsonLineReader.MaxLineBytes, bytes => IsRelevantLine(bytes, source));
        state.SessionIdentityRead = true;
        if (source == "API") state.ApiMetadataVersion = 2;
        if (source is "Claude" or "Codex") state.ProjectMetadataVersion = 1;
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

    private bool ProcessDsh(string path)
    {
        static string Signature(string file)
        {
            var info = new FileInfo(file);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{info.CreationTimeUtc.Ticks}";
        }
        var signature = Signature(path);
        if (!cache.Files.TryGetValue(path, out var state)) state = new FileState { Source = DshUsage.SourceName };
        if (state.Head == signature) return false;
        Hydrate(state);
        var records = DshUsage.Read(path);
        foreach (var record in records) state.Records[record.Id] = record;
        state.Head = Signature(path) == signature ? signature : "";
        cache.Files[path] = state;
        return true;
    }

    private static bool IsRelevantLine(ReadOnlyMemory<byte> line, string source) => source switch
    {
        "Claude" => line.Span.IndexOf("\"usage\""u8) >= 0 || line.Span.IndexOf("\"cwd\""u8) >= 0,
        PiDesktopUsage.SourceName or WorkBuddyUsage.SourceName => line.Span.IndexOf("\"usage\""u8) >= 0,
        "Codex" => line.Span.IndexOf("\"token_count\""u8) >= 0 ||
            line.Span.IndexOf("\"turn_context\""u8) >= 0 || line.Span.IndexOf("\"session_meta\""u8) >= 0,
        _ => true
    };

    private static void Consume(string line, FileState state)
    {
        if (state.Source == "Claude" && !line.Contains("\"usage\"", StringComparison.Ordinal) && !line.Contains("\"cwd\"", StringComparison.Ordinal)) return;
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
            else if (state.Source == WorkBuddyUsage.SourceName)
            {
                if (WorkBuddyUsage.Parse(document.RootElement) is { } record) state.Records[record.Id] = record;
            }
            else ConsumeApi(document.RootElement, state);
        }
        catch (JsonException) { state.Skipped++; }
    }

    private static void ConsumeClaude(JsonElement root, FileState state)
    {
        ReadCwd(root, state);
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
            input + read + write, read, write, output, think, Cwd: state.Cwd);
    }

    private static void ConsumeCodex(JsonElement root, FileState state)
    {
        if (!root.TryGetProperty("payload", out var payload)) return;
        var type = root.StringOrEmpty("type");
        if (type is "turn_context" or "session_meta")
        {
            ReadCwd(payload, state);
            if (type == "session_meta" && payload.StringOrEmpty("id") is { Length: > 0 } sessionId)
                state.SessionId = sessionId;
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
        var id = state.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        StoreSequencedRecord(state, new(
            id, "Codex", model.Length == 0 ? "Codex" : model, timestamp.Value,
            usage["input_tokens"], usage["cached_input_tokens"], usage["cache_write_input_tokens"],
            usage["output_tokens"], usage["reasoning_output_tokens"], Cwd: state.Cwd));
    }

    private static bool? ReadBoolean(JsonElement root, string key) => root.TryGetProperty(key, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static void ReadCwd(JsonElement root, FileState state)
    {
        if (root.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String &&
            !cwd.ValueEquals("") && !cwd.ValueEquals(state.Cwd)) state.Cwd = cwd.GetString()!;
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
        var id = state.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        StoreSequencedRecord(state, new(
            id, "API · " + provider, model.Length == 0 ? "?" : model, timestamp.Value,
            NonNegative(root, "ctx"), NonNegative(root, "cache_read"), NonNegative(root, "cache_write"),
            NonNegative(root, "out"), NonNegative(root, "think"),
            (int)NonNegative(root, "status"), (int)NonNegative(root, "ms"),
            root.TryGetProperty("parsed", out var parsed) && parsed.ValueKind is JsonValueKind.True or JsonValueKind.False ? parsed.GetBoolean() : null,
            ReadBoolean(root, "reached_upstream") ?? ReadBoolean(root, "sent"),
            root.TryGetProperty("rl", out var rl) && rl.ValueKind == JsonValueKind.Object ? ApiQuality.SafeHeaders(rl.EnumerateObject()
                .Where(x => x.Value.ValueKind == JsonValueKind.String).Select(x => new KeyValuePair<string, string>(x.Name, x.Value.GetString()!))) : null,
            root.TryGetProperty("complete", out var complete) && complete.ValueKind is JsonValueKind.True or JsonValueKind.False ? complete.GetBoolean() : null, host));
    }

    private UsageScanResult Summarize(IReadOnlyCollection<string> errors)
    {
        var start = new DateTimeOffset(DateTime.Today);
        var claudeFiles = CachedFiles("Claude");
        var claude = SourceRecords("Claude", () => MostRecentPerId(claudeFiles.SelectMany(x => x.Value.Records.Values)));
        var codexFiles = CachedFiles("Codex");
        var codex = SourceRecords("Codex", () => CodexRecords(codexFiles));
        var piFiles = CachedFiles(PiDesktopUsage.SourceName);
        var pi = SourceRecords(PiDesktopUsage.SourceName, () => PiRecords(piFiles));
        var zcodeFiles = CachedFiles(ZCodeUsage.SourceName);
        var zcode = SourceRecords(ZCodeUsage.SourceName, () => MostRecentPerId(zcodeFiles.SelectMany(x => x.Value.Records.Values)));
        var buddyFiles = CachedFiles(WorkBuddyUsage.SourceName);
        var buddy = SourceRecords(WorkBuddyUsage.SourceName, () => MostRecentPerId(buddyFiles.SelectMany(x => x.Value.Records.Values)));
        var dshFiles = CachedFiles(DshUsage.SourceName);
        var dsh = SourceRecords(DshUsage.SourceName, () => MostRecentPerId(dshFiles.SelectMany(x => x.Value.Records.Values)));
        var apiFiles = CachedFiles("API");
        var api = SourceRecords("API", () => MergeApiFileCopies(apiFiles));

        var now = DateTimeOffset.Now;
        var todayClaude = claude.Where(x => x.Timestamp >= start && x.Timestamp <= now).ToArray();
        var todayCodex = codex.Where(x => x.Timestamp >= start && x.Timestamp <= now).ToArray();
        var todayPi = pi.Where(x => x.Timestamp >= start && x.Timestamp <= now).ToArray();
        var todayZCode = zcode.Where(x => x.Timestamp >= start && x.Timestamp <= now).ToArray();
        var todayBuddy = buddy.Where(x => x.Timestamp >= start && x.Timestamp <= now).ToArray();
        var todayDsh = dsh.Where(x => x.Timestamp >= start && x.Timestamp <= now).ToArray();
        var buddyDetected = buddyFiles.Length > 0 || Directory.Exists(paths.WorkBuddyRoot) || Directory.Exists(paths.WorkBuddyAiRoot);
        var dshDetected = dshFiles.Length > 0 || Directory.Exists(paths.DshRoot);
        var cliRecords = todayClaude.Concat(todayCodex).Concat(todayPi).Concat(todayZCode).Concat(todayBuddy).Concat(todayDsh).ToArray();
        var sourceRows = new[]
        {
            SourceSummary("Claude Code", todayClaude, claudeFiles.Length > 0, "本地无今日 token 统计"),
            SourceSummary("Codex", todayCodex, codexFiles.Length > 0, "本地无今日 token 统计"),
            SourceSummary(PiDesktopUsage.SourceName, todayPi, piFiles.Length > 0 || Directory.Exists(paths.PiDesktopRoot), "本地无今日 token 统计"),
            SourceSummary(ZCodeUsage.SourceName, todayZCode, zcodeFiles.Length > 0 || Directory.Exists(paths.ZCodeRoot), "本地无今日 token 统计"),
            SourceSummary(WorkBuddyUsage.SourceName, todayBuddy, buddyDetected, "本地无今日 token 统计"),
            SourceSummary(DshUsage.SourceName, todayDsh, dshDetected, "本地无今日 token 统计"),
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
            error, BuildProjects(todayClaude.Concat(todayCodex)));

        api = api.Where(x => x.Timestamp <= now).ToArray();
        var todayApi = api.Where(x => x.Timestamp >= start).ToArray();
        var prices = PriceTable.Load(paths);
        var ranges = new[]
        {
            BuildApiRange("today", "今日", todayApi),
            BuildApiRange("7d", "7D", api.Where(x => x.Timestamp >= DateTimeOffset.Now.AddDays(-7)).ToArray()),
            BuildApiRange("30d", "30D", api.Where(x => x.Timestamp >= DateTimeOffset.Now.AddDays(-30)).ToArray()),
            BuildApiRange("all", "All", api)
        }.Select(r => r with { Cost = prices.Estimate(r.Models.Select(m => new ModelMix(m.Provider, m.Model, m.Calls,
            m.ContextTokens, m.OutputTokens, m.CacheReadTokens, m.CacheWriteTokens, m.ThinkingTokens)), r.UnknownUsage) }).ToArray();
        var providerRows = ranges[0].Providers;
        var apiState = errors.Any(x => x.StartsWith("API ", StringComparison.Ordinal)) && apiFiles.Length > 0 ? UsageDataState.ReadFailed :
            apiFiles.Length == 0 ? UsageDataState.NotDetected :
            todayApi.Length == 0 ? UsageDataState.NoLocalStats : UsageDataState.Available;
        var apiSummary = new ApiUsageSummary(
            apiState, todayApi.Length, todayApi.Sum(x => x.ContextTokens), todayApi.Sum(x => x.OutputTokens),
            todayApi.Sum(x => x.ThinkingTokens), providerRows,
            apiState switch
            {
                UsageDataState.Available => todayApi.Any(x => x.UnknownUsage) ? $"已记录用量；另有 {todayApi.Count(x => x.UnknownUsage)} 次成功调用用量未知" : "来自本地 API 调用日志",
                UsageDataState.NoLocalStats => "今日暂无 API 调用",
                UsageDataState.ReadFailed => "部分 API 日志读取失败",
                _ => "未检测到 API 日志"
            },
            ranges,
            api.OrderByDescending(x => x.Timestamp).Take(8)
                .Select(x => new ApiRecentCall(
                    x.ApiAccount,
                    x.Model,
                    x.Timestamp,
                    x.TotalTokens,
                    x.Status,
                    x.LatencyMs, x.UnknownUsage))
                .ToArray(), todayApi.Count(x => x.UnknownUsage));
        var archivedPi = pi.Where(x => x.Timestamp <= now).ToArray();
        var piTotal = SourceSummary(PiDesktopUsage.SourceName, archivedPi,
            piFiles.Length > 0 || Directory.Exists(paths.PiDesktopRoot), "本地无 token 统计");
        var archivedZCode = zcode.Where(x => x.Timestamp <= now).ToArray();
        var zcodeTotal = SourceSummary(ZCodeUsage.SourceName, archivedZCode,
            zcodeFiles.Length > 0 || Directory.Exists(paths.ZCodeRoot), "本地无 token 统计");
        if (errors.FirstOrDefault(x => x.StartsWith("ZCode", StringComparison.Ordinal)) is { } zcodeError)
            zcodeTotal = zcodeTotal with { State = UsageDataState.ReadFailed, Note = zcodeError };
        var archivedBuddy = buddy.Where(x => x.Timestamp <= now).ToArray();
        var archivedDsh = dsh.Where(x => x.Timestamp <= now).ToArray();
        var buddyTotal = SourceSummary(WorkBuddyUsage.SourceName, archivedBuddy, buddyDetected, "本地无 token 统计");
        var dshTotal = SourceSummary(DshUsage.SourceName, archivedDsh, dshDetected, "本地无 token 统计");
        if (errors.FirstOrDefault(x => x.StartsWith(WorkBuddyUsage.SourceName + " ", StringComparison.Ordinal)) is { } buddyError)
            buddyTotal = buddyTotal with { State = UsageDataState.ReadFailed, Note = buddyError };
        if (errors.FirstOrDefault(x => x.StartsWith(DshUsage.SourceName + " ", StringComparison.Ordinal)) is { } dshError)
            dshTotal = dshTotal with { State = UsageDataState.ReadFailed, Note = dshError };
        var statistics = UsageStatistics.Build(claude.Concat(codex).Concat(archivedPi).Concat(archivedZCode).Concat(archivedBuddy).Concat(archivedDsh), now, apiRecords: api);
        return new(cli, apiSummary, statistics.WithHistory(cache.ColdStatistics) with { Prices = prices },
            WithColdTotal(piTotal), WithColdTotal(zcodeTotal), WithColdTotal(buddyTotal), WithColdTotal(dshTotal), CodingPlan.LoadEstimates(paths, api, now));
    }

    private InteractionRecord[] SourceRecords(string source, Func<IEnumerable<InteractionRecord>> build)
    {
        if (!sourceRecords.TryGetValue(source, out var records)) sourceRecords[source] = records = build().ToArray();
        return records;
    }

    private KeyValuePair<string, FileState>[] CachedFiles(string source) =>
        cache.Files.Where(x => x.Value.Source == source)
            .OrderByDescending(x => x.Value.LastWriteUtcTicks)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray();

    private static void StoreSequencedRecord(FileState state, InteractionRecord record)
    {
        // Codex/API IDs are file-local sequence numbers, not provider message IDs.
        // Keep replay matches across incremental reads and restarts: a truncated
        // file may be restored in chunks. Counts preserve identical real calls.
        if (state.ReplayRemaining.Count > 0)
        {
            var key = ReplayFingerprint(record, state.Source);
            // Only metadata-less legacy rows may adopt a newly discovered host.
            // A known host is part of identity when a log is rewritten or restored.
            if (state.Source == "API" && !state.ReplayRemaining.ContainsKey(key) && record.ApiHost.Length > 0)
                key = UsageFingerprint(UsageIdentity.From(record));
            if (state.ReplayRemaining.TryGetValue(key, out var count))
            {
                if (state.Source is "API" or "Codex")
                {
                    state.ReplayMetadata ??= state.Records.Values.GroupBy(x => ReplayFingerprint(x, state.Source))
                        .ToDictionary(g => g.Key, g => new Queue<string>(g.Select(x => x.Id).Skip(Math.Max(0, g.Count() - state.ReplayRemaining.GetValueOrDefault(g.Key)))));
                    if (state.ReplayMetadata.TryGetValue(key, out var ids) && ids.TryDequeue(out var existingId))
                    {
                        var existing = state.Records[existingId];
                        state.Records[existingId] = record with { Id = existingId, UsageKnown = record.UsageKnown ?? existing.UsageKnown,
                            ApiHost = record.ApiHost.Length > 0 ? record.ApiHost : existing.ApiHost,
                            ReachedUpstream = record.ReachedUpstream ?? existing.ReachedUpstream, Completed = record.Completed ?? existing.Completed,
                            RateLimits = record.RateLimits is { Count: > 0 } ? record.RateLimits : existing.RateLimits,
                            Cwd = record.Cwd.Length > 0 ? record.Cwd : existing.Cwd };
                    }
                }
                if (count <= 1) state.ReplayRemaining.Remove(key);
                else state.ReplayRemaining[key] = count - 1;
                if (state.ReplayRemaining.Count == 0) state.ReplayMetadata = null;
                return;
            }
        }
        state.Records[record.Id] = record;
        state.Sequence++;
    }

    private static string ReplayFingerprint(InteractionRecord record, string source) =>
        UsageFingerprint(UsageIdentity.From(record)) + (source == "API" && record.ApiHost.Length > 0 ? "|" + record.ApiHost.ToUpperInvariant() : "");

    private static string UsageFingerprint(UsageIdentity identity) =>
        Fingerprint(JsonSerializer.SerializeToUtf8Bytes(identity));

    private static IEnumerable<InteractionRecord> CodexRecords(KeyValuePair<string, FileState>[] files)
    {
        foreach (var session in files.GroupBy(CodexSessionKey, StringComparer.Ordinal))
            foreach (var record in MergeFileCopies(session.ToArray())) yield return record;
    }

    private static IEnumerable<InteractionRecord> MergeApiFileCopies(KeyValuePair<string, FileState>[] files)
    {
        // Legacy retained rows have no host. Match them against enriched copies first,
        // but keep separate known hosts even when all usage fields happen to match.
        foreach (var identity in files.SelectMany(file => file.Value.Records.Values.Select(row => (File: file.Key, Row: row)))
                     .GroupBy(x => UsageIdentity.From(x.Row)))
        {
            var knownCount = 0;
            foreach (var host in identity.Where(x => x.Row.ApiHost.Length > 0).GroupBy(x => x.Row.ApiHost, StringComparer.OrdinalIgnoreCase))
            {
                var representative = host.GroupBy(x => x.File).OrderByDescending(x => x.Count()).First();
                foreach (var item in representative) { knownCount++; yield return item.Row; }
            }
            var unknown = identity.Where(x => x.Row.ApiHost.Length == 0).GroupBy(x => x.File).OrderByDescending(x => x.Count()).FirstOrDefault();
            if (unknown is not null) foreach (var item in unknown.Skip(knownCount)) yield return item.Row;
        }
    }

    private static IEnumerable<InteractionRecord> MergeFileCopies(KeyValuePair<string, FileState>[] files)
    {
        if (files.Length == 1)
        {
            foreach (var record in files[0].Value.Records.Values) yield return record;
            yield break;
        }
        var seen = new Dictionary<UsageIdentity, List<InteractionRecord>>();
        foreach (var file in files)
        {
            var occurrences = new Dictionary<UsageIdentity, int>();
            foreach (var record in file.Value.Records.Values)
            {
                var identity = UsageIdentity.From(record);
                var occurrence = occurrences.GetValueOrDefault(identity) + 1;
                occurrences[identity] = occurrence;
                if (!seen.TryGetValue(identity, out var copies)) seen[identity] = copies = [];
                if (occurrence > copies.Count) copies.Add(record);
                else if (copies[occurrence - 1].Cwd.Length == 0 && record.Cwd.Length > 0)
                    copies[occurrence - 1] = record;
            }
        }
        foreach (var copies in seen.Values)
            foreach (var record in copies) yield return record;
    }

    private static string CodexSessionKey(KeyValuePair<string, FileState> file)
    {
        var id = file.Value.SessionId;
        if (id.Length > 0) return "session:" + (Guid.TryParse(id, out var parsed) ? parsed.ToString("D") : id);
        // Version-1 caches did not store session metadata. Standard rollout file
        // names contain the session UUID, including files already deleted on disk.
        var name = Path.GetFileNameWithoutExtension(file.Key);
        if (name.Length >= 36 && Guid.TryParse(name[^36..], out var legacyId))
            return "session:" + legacyId.ToString("D");
        return "path:" + file.Key.ToUpperInvariant();
    }

    private readonly record struct UsageIdentity(
        string Source, string Model, long TimestampTicks,
        long Context, long Read, long Write, long Output, long Thinking, int Status, int Latency)
    {
        public static UsageIdentity From(InteractionRecord value) => new(
            value.Source, value.Model, value.Timestamp.UtcTicks, value.ContextTokens, value.CacheReadTokens,
            value.CacheWriteTokens, value.OutputTokens, value.ThinkingTokens, value.Status, value.LatencyMs);
    }

    private static IEnumerable<InteractionRecord> PiRecords(IEnumerable<KeyValuePair<string, FileState>> files) =>
        MostRecentPerId(files.SelectMany(x => x.Value.Records.Values));

    private static IEnumerable<InteractionRecord> MostRecentPerId(IEnumerable<InteractionRecord> records)
    {
        var latest = new Dictionary<string, InteractionRecord>(StringComparer.Ordinal);
        foreach (var record in records)
            if (!latest.TryGetValue(record.Id, out var old) || old.Timestamp < record.Timestamp ||
                old.Timestamp == record.Timestamp && old.Cwd.Length == 0 && record.Cwd.Length > 0)
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
        var providers = records.GroupBy(x => x.ApiAccount, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ApiProviderSummary(
                group.Key,
                group.Count(),
                group.Sum(x => x.ContextTokens),
                group.Sum(x => x.CacheReadTokens),
                group.Sum(x => x.CacheWriteTokens),
                group.Sum(x => x.OutputTokens),
                group.Sum(x => x.ThinkingTokens),
                group.Count(x => x.Failed),
                group.Count() == 0 ? 0 : (int)Math.Round(group.Average(x => x.LatencyMs)), group.Count(x => x.UnknownUsage), ApiQuality.Build(group)))
            .OrderByDescending(x => x.Calls)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var models = records.GroupBy(
                x => (Provider: x.ApiAccount, Model: x.Model),
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
                group.Count(x => x.Failed),
                group.Count() == 0 ? 0 : (int)Math.Round(group.Average(x => x.LatencyMs)), group.Count(x => x.UnknownUsage)))
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
            records.Count(x => x.Failed),
            records.Count == 0 ? 0 : (int)Math.Round(records.Average(x => x.LatencyMs)),
            providers,
            models, records.Count(x => x.UnknownUsage), ApiQuality.Build(records));
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
            var inputPath = File.Exists(cachePath) ? cachePath : Path.Combine(paths.LocalDataRoot, "usage-incremental.json");
            if (File.Exists(inputPath))
            {
                using var input = File.OpenRead(inputPath);
                cache = JsonSerializer.Deserialize<ScannerCache>(input) ?? new();
                if (cache.Version is not (1 or ScannerCache.CurrentVersion)) throw new InvalidDataException("Unsupported usage ledger version");
                cacheDirty = inputPath != cachePath || cache.Version != ScannerCache.CurrentVersion;
                cache.Version = ScannerCache.CurrentVersion;
                ShareRetainedStrings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { loaded = false; throw new IOException("历史账本读取失败；为保护已留存用量，未重建或覆盖账本。", e); }
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

    private void ShareRetainedStrings()
    {
        // Scoped to this ledger load; never intern user data for the process lifetime.
        var strings = new HashSet<string>(StringComparer.Ordinal);
        string Share(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (strings.TryGetValue(value, out var existing)) return existing;
            if (strings.Count < 8192) strings.Add(value);
            return value;
        }
        foreach (var file in cache.Files.Values)
        {
            file.Source = Share(file.Source); file.Model = Share(file.Model); file.Cwd = Share(file.Cwd);
            foreach (var (key, row) in file.Records.ToArray())
                file.Records[key] = row with { Id = key == row.Id ? key : row.Id,
                    Source = Share(row.Source), Model = Share(row.Model), ApiHost = Share(row.ApiHost), Cwd = Share(row.Cwd) };
        }
    }

    private sealed record DiscoveredFile(string Path, string Source);

    private sealed class ScannerCache
    {
        public const int CurrentVersion = 2;
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public UsageStatistics? ColdStatistics { get; set; }
        public Dictionary<string, UsageSourceSummary> ColdTotals { get; set; } = [];
        public string ColdTimeZone { get; set; } = "";
        public byte[] ColdBloom { get; set; } = [];
    }

    private sealed class FileState
    {
        public string Source { get; set; } = "";
        public string Model { get; set; } = "?";
        public string SessionId { get; set; } = "";
        public bool SessionIdentityRead { get; set; }
        public int ApiMetadataVersion { get; set; }
        public int ProjectMetadataVersion { get; set; }
        public string Cwd { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore] public Dictionary<string, Queue<string>>? ReplayMetadata { get; set; }
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
        public Dictionary<string, int> ReplayRemaining { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, InteractionRecord> Records { get; set; } = new(StringComparer.Ordinal);
        public string PackedRecords { get; set; } = "";
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
