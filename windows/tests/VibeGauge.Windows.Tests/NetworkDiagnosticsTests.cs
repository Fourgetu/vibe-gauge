using System.Text.Json;
using VibeGauge.Windows.Services;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class NetworkDiagnosticsTests
{
    [Fact]
    public void ControllerSnapshotOnlyExposesGroupSelectionsAndAiHostChains()
    {
        using var doc = JsonDocument.Parse("""{"secret":"private","proxies":{"AI":{"now":"chosen","password":"secret"}},"connections":[{"metadata":{"host":"api.anthropic.com","processPath":"private-path"},"chains":["AI","chosen"]},{"metadata":{"host":"private.example"},"chains":["secret"]}]}""");
        var rows = NetworkDiagnostics.ParseClash(doc.RootElement);
        Assert.Equal(2, rows.Count);
        var text = string.Join("\n", rows);
        Assert.Contains("api.anthropic.com", text); Assert.Contains("chosen", text);
        Assert.DoesNotContain("private", text); Assert.DoesNotContain("secret", text);
    }
}
