using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class ReauditRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-reaudit-win-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));
    private static ProcessReport Report(Dictionary<int, ProcessSnapshot> processes, HashSet<int>? listeners, HashSet<int>? services) =>
        (ProcessReport)typeof(WindowsSystemScanner).GetMethod("BuildProcessReport", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [processes, listeners, services, Array.Empty<string>()])!;
    [Fact]
    public void OrphanWithActiveChildIsProtectedAndNoRealProcessIsTerminated()
    {
        using var current = Process.GetCurrentProcess();
        var processes = new Dictionary<int, ProcessSnapshot> {
            [901101] = new(901101, 909999, "node.exe", "node fixture-mcp-server", @"C:\fixture\node.exe", 10, DateTimeOffset.Now, current.SessionId),
            [901102] = new(901102, 901101, "node.exe", "node child-http-service", @"C:\fixture\node.exe", 10, DateTimeOffset.Now, current.SessionId) };
        Assert.Empty(Report(processes, [901102], []).Orphans);
        Assert.Empty(Report(processes, [], []).Orphans);
        processes.Remove(901102); Assert.Single(Report(processes, [], []).Orphans);
        Assert.Empty(Report(processes, null, []).Orphans); Assert.Empty(Report(processes, [], null).Orphans);
    }
    [Theory]
    [InlineData("claude.ai", "Claude")]
    [InlineData("gemini.google.com", "Gemini")]
    [InlineData("generativelanguage.googleapis.com", "Gemini")]
    [InlineData("cloudcode-pa.googleapis.com", "Gemini")]
    [InlineData("auth.openai.com", "ChatGPT / Codex")]
    [InlineData(" GEMINI.GOOGLE.COM. ", "Gemini")]
    [InlineData("api.openai.com", "OpenAI API")]
    [InlineData("chatgpt.com", "ChatGPT / Codex")]
    public void ConnectionChainsAreAttachedToTheirAiWithoutSecrets(string host, string provider)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { secret = "never-show", connections = new[] {
            new { metadata = new { host, processPath = "never-show" }, chains = new[] { "AI", "node" } } } }));
        var row = Assert.Single(NetworkDiagnostics.ParseConnections(doc.RootElement));
        Assert.Equal(provider, row.Provider); Assert.Equal("AI → node", row.Chain);
        Assert.DoesNotContain("never-show", JsonSerializer.Serialize(row));
    }
    [Fact]
    public void OpenAiHasIndependentTraceTargetAndGeminiUsesConnectionTable()
    {
        Assert.Contains(NetworkDiagnostics.Targets, x => x.Host == "api.openai.com");
        Assert.DoesNotContain(NetworkDiagnostics.Targets, x => x.Provider is "Gemini" or "Grok");
    }
    [Fact]
    public void ConnectionParserAcceptsLegacyHostAndMissingChainWithoutTruncatingAiRows()
    {
        using var doc = JsonDocument.Parse("{\"connections\":[" + string.Join(',', Enumerable.Repeat("{\"host\":\"unrelated.example\"}", 250)) +
            ",{\"host\":\"gemini.google.com\",\"chains\":[]}]}" );
        var row = Assert.Single(NetworkDiagnostics.ParseConnections(doc.RootElement));
        Assert.Equal("Gemini", row.Provider); Assert.Empty(row.Chain);
    }
    private static NetworkDiagnosticsReport Snapshot(string ip, string region = "US", string error = "") =>
        new(DateTimeOffset.Now, [new("OpenAI API", "api.openai.com", ip, region, "TEST", error)], [], [], [], "fixture", "fixture");
    [Fact]
    public async Task EgressBaselineSurvivesFailuresManualScheduledRefreshAndRestart()
    {
        var next = Snapshot("192.0.2.1");
        var service = new NetworkDiagnostics(Paths, () => Task.FromResult(next));
        Assert.Empty((await service.RefreshAsync()).Changes);
        next = Snapshot("", error: "failed");
        Assert.Empty((await service.RefreshAsync()).Changes); Assert.Empty(service.Scan()!.Exits[0].Ip);
        next = Snapshot("192.0.2.2");
        Assert.Single((await service.RefreshAsync()).Changes);
        Assert.Equal(next.Exits, service.Scan()!.Exits);
        service = new NetworkDiagnostics(Paths, () => Task.FromResult(next));
        Assert.Empty((await service.RefreshAsync()).Changes);
        next = Snapshot("192.0.2.2", "JP");
        Assert.Single((await service.RefreshAsync()).Changes);
        next = Snapshot("192.0.2.3", "JP");
        new FeaturePreferences { NetworkDiagnostics = true }.Save(Paths);
        service = new NetworkDiagnostics(Paths, () => Task.FromResult(next));
        Assert.Single(service.Scan()!.Changes);
        Assert.Empty((await service.RefreshAsync()).Changes);
    }
    [Fact]
    public async Task SimultaneousManualRefreshesShareOneCollectionAndOptInIsRequiredForScheduledProbe()
    {
        var calls = 0;
        var completion = new TaskCompletionSource<NetworkDiagnosticsReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new NetworkDiagnostics(Paths, () => { calls++; return completion.Task; });
        Assert.Null(service.Scan()); Assert.Equal(0, calls);
        var a = service.RefreshAsync(); var b = service.RefreshAsync(); Assert.Same(a, b); Assert.Equal(1, calls);
        completion.SetResult(Snapshot("192.0.2.1")); await a;
        Assert.NotNull(service.Scan()); Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData(0, false, "无有效出口结果")]
    [InlineData(1, false, "仅一个有效结果，无法比较")]
    [InlineData(2, false, "已确认的出口一致")]
    [InlineData(2, true, "各站点出口不同")]
    public void ComparisonDoesNotClaimAgreementWithoutMultipleSuccessfulResults(int successes, bool different, string expected)
    {
        var exits = Enumerable.Range(0, 3).Select(i => new EgressInfo("AI" + i, "fixture", i < successes ? "192.0.2." + (different ? i + 1 : 1) : "",
            "US", "TEST", i < successes ? "" : "failed")).ToArray();
        var report = Snapshot("") with { Exits = exits };
        Assert.Contains(expected, report.Comparison); Assert.Contains($"成功 {successes}/3", report.Comparison);
    }
    [Fact]
    public void ArkNpmDiscoveryUsesValidatedNativeLayoutAndRejectsArbitraryShims()
    {
        var npm = Path.Combine(root, "npm"); var package = Path.Combine(npm, "node_modules", "@volcengine", "ark-cli");
        Directory.CreateDirectory(Path.Combine(package, "bin")); File.WriteAllText(Path.Combine(npm, "arkcli.cmd"), "exit /b 42");
        Assert.Null(OfficialSources.FindExecutable(Paths, "arkcli", [npm]));
        var native = Path.Combine(package, "bin", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "arkcli-windows-arm64.exe" : "arkcli-windows-amd64.exe");
        File.WriteAllText(native, "fixture; never executed");
        var manifest = Path.Combine(package, "package.json");
        File.WriteAllText(manifest, """{"name":"@volcengine/ark-cli","bin":{"arkcli":"scripts/run.js"}}""");
        Assert.Equal(native, OfficialSources.FindExecutable(Paths, "arkcli", [npm]));
        File.WriteAllText(manifest, """{"name":"@volcengine/ark-cli","bin":{"arkcli":"../../unexpected.js"}}""");
        Assert.Null(OfficialSources.FindExecutable(Paths, "arkcli", [npm]));
        File.WriteAllText(manifest, "{broken"); Assert.Null(OfficialSources.FindExecutable(Paths, "arkcli", [npm]));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
