using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ProjectUsageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-projects-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));
    private readonly DateTimeOffset now = DateTimeOffset.Now.AddSeconds(-5);
    private static void Write(string path, params string[] rows)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, string.Join('\n', rows) + "\n"); }
    private string Claude(string id, string cwd, DateTimeOffset? timestamp = null) => JsonSerializer.Serialize(new
    { type = "assistant", requestId = id, cwd, timestamp = timestamp ?? now, message = new { model = "claude", usage = new
        { input_tokens = 100, cache_read_input_tokens = 200, cache_creation_input_tokens = 50, output_tokens = 20 } } });
    private static string Meta(string id, string cwd) => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, cwd } });
    private string Codex(DateTimeOffset? timestamp = null) => JsonSerializer.Serialize(new { type = "event_msg", timestamp = timestamp ?? now,
        payload = new { type = "token_count", info = new { last_token_usage = new { input_tokens = 350, output_tokens = 30 } } } });

    [Fact]
    public void ProjectsMergeToolsAndDeduplicateCopiesWithoutRemovingAgentTotals()
    {
        Write(Path.Combine(Paths.ClaudeRoot, "projects", "a.jsonl"), Claude("a", "C:\\Work\\app\\"), Claude("b", "D:\\Other\\app"),
            Claude("old", "C:\\Yesterday", new DateTimeOffset(DateTime.Today).AddSeconds(-1)), Claude("future", "C:\\Future", now.AddDays(1)));
        Write(Path.Combine(Paths.ClaudeRoot, "projects", "copy.jsonl"), Claude("a", "C:\\Work\\app\\"));
        Write(Path.Combine(Paths.CodexRoot, "sessions", "a.jsonl"), Meta("session-a", "c:/work/app"), Codex());
        Write(Path.Combine(Paths.CodexRoot, "archived_sessions", "a.jsonl"), Meta("session-a", "c:/work/app"), Codex());
        var scanner = new UsageScanner(Paths);
        var usage = scanner.ScanToday();
        Assert.Equal(3, usage.Turns); Assert.Equal(1120, usage.TotalTokens);
        Assert.Equal(2, usage.Projects!.Count);
        var first = usage.Projects[0];
        Assert.Equal(700, first.ContextTokens); Assert.Equal(50, first.OutputTokens); Assert.Equal(2, first.Turns);
        Assert.Equal(new[] { "Claude", "Codex" }, first.Contributors);
        Assert.All(usage.Projects, x => Assert.Equal("app", x.DisplayName));
        Assert.Equal(2, usage.Sources.Single(x => x.Name == "Claude Code").Turns);
        Assert.Equal(1, usage.Sources.Single(x => x.Name == "Codex").Turns);
        Assert.Equal(usage.TotalTokens, usage.Projects.Sum(x => x.TotalTokens));
        Assert.Equal(0, new UsageScanner(Paths).ScanToday().Error.Length);
        Assert.Equal(usage.TotalTokens, scanner.ScanToday().TotalTokens);
        Assert.Equal(0, scanner.LastDiagnostics.FilesRead);
    }

    [Fact]
    public void LegacyCacheReplaysMetadataOnceWithoutAddingConsumption()
    {
        Write(Path.Combine(Paths.ClaudeRoot, "projects", "a.jsonl"), Claude("a", "C:\\Project"));
        Write(Path.Combine(Paths.CodexRoot, "sessions", "a.jsonl"), Meta("s", "C:\\Project"), Codex(), Codex());
        var original = new UsageScanner(Paths).ScanToday();
        var cacheFile = Path.Combine(Paths.LocalDataRoot, UsageScanner.CacheFileName);
        var cache = JsonNode.Parse(File.ReadAllText(cacheFile))!;
        foreach (var file in cache["Files"]!.AsObject())
        {
            file.Value!.AsObject().Remove("Cwd"); file.Value.AsObject().Remove("ProjectMetadataVersion");
            foreach (var row in file.Value["Records"]!.AsObject()) row.Value!.AsObject().Remove("Cwd");
        }
        File.WriteAllText(cacheFile, cache.ToJsonString());
        var scanner = new UsageScanner(Paths);
        var migrated = scanner.ScanToday();
        Assert.Equal(original.TotalTokens, migrated.TotalTokens); Assert.Equal(3, migrated.Turns);
        Assert.Equal("C:/Project", Assert.Single(migrated.Projects!).Path);
        Assert.Equal(3, migrated.Projects![0].Turns);
        Assert.Equal(original.TotalTokens, new UsageScanner(Paths).ScanToday().TotalTokens);
        scanner.Scan(); Assert.Equal(0, scanner.LastDiagnostics.FilesRead);
    }

    [Fact]
    public void MissingPathsStayUnknownAndTurnContextCanChangeProject()
    {
        Write(Path.Combine(Paths.ClaudeRoot, "projects", "a.jsonl"), Claude("a", ""));
        Write(Path.Combine(Paths.CodexRoot, "sessions", "a.jsonl"), Meta("s", "C:\\A"), Codex(),
            """{"type":"turn_context","payload":{"cwd":"C:\\B"}}""", Codex(now.AddSeconds(1)));
        var usage = new UsageScanner(Paths).ScanToday();
        Assert.Equal(3, usage.Projects!.Count);
        Assert.Contains(usage.Projects, x => x.Path == "" && x.DisplayName == "未知项目");
        Assert.Contains(usage.Projects, x => x.Path == "C:/A"); Assert.Contains(usage.Projects, x => x.Path == "C:/B");
    }

    [Fact]
    public void ClaudeUserCwdIsReadEvenWithoutUsageAndDeletedLegacyLogsAreNotInvented()
    {
        var file = Path.Combine(Paths.ClaudeRoot, "projects", "a.jsonl");
        Write(file, """{"type":"user","cwd":"C:\\FromUser"}""", Claude("a", ""));
        Assert.Equal("C:/FromUser", Assert.Single(new UsageScanner(Paths).ScanToday().Projects!).Path);
        var cacheFile = Path.Combine(Paths.LocalDataRoot, UsageScanner.CacheFileName);
        var cache = JsonNode.Parse(File.ReadAllText(cacheFile))!;
        foreach (var state in cache["Files"]!.AsObject())
            foreach (var row in state.Value!["Records"]!.AsObject()) row.Value!.AsObject().Remove("Cwd");
        File.WriteAllText(cacheFile, cache.ToJsonString()); File.Delete(file);
        var usage = new UsageScanner(Paths).ScanToday();
        Assert.Equal(370, usage.TotalTokens); Assert.Equal("", Assert.Single(usage.Projects!).Path);
    }

    [Fact]
    public void PosixPathsRemainCaseSensitiveWhileWindowsPathsMerge()
    {
        Write(Path.Combine(Paths.ClaudeRoot, "projects", "a.jsonl"), Claude("a", "/work/App"), Claude("b", "/work/app"),
            Claude("c", "C:\\Work\\APP"), Claude("d", "c:/work/app/"));
        var projects = new UsageScanner(Paths).ScanToday().Projects!;
        Assert.Equal(3, projects.Count); Assert.Equal(2, projects[0].Turns);
    }

    [Fact]
    public void MetadataFromAnyCodexCopyIsKeptWithoutChangingRealOccurrenceCounts()
    {
        var known = Path.Combine(Paths.CodexRoot, "sessions", "known.jsonl");
        var unknown = Path.Combine(Paths.CodexRoot, "archived_sessions", "unknown.jsonl");
        Write(known, Meta("s", "C:\\Known"), Codex(), Codex());
        Write(unknown, Meta("s", ""), Codex(), Codex());
        File.SetLastWriteTimeUtc(unknown, DateTime.UtcNow.AddMinutes(1));
        var usage = new UsageScanner(Paths).ScanToday();
        Assert.Equal(2, usage.Turns); Assert.Equal(760, usage.TotalTokens);
        Assert.Equal("C:/Known", Assert.Single(usage.Projects!).Path);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
