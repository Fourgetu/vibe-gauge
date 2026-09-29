using System.IO;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class ClientVisibilityTests
{
    [Fact]
    public void VisibilityPersistsWithoutTouchingOtherSettingsAndCorruptionDefaultsToVisible()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-visibility-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new ClientVisibilitySettings(root);
            Assert.Empty(settings.Load());
            Assert.True(new TokenUnitSettings(root).Save(TokenUnit.International));
            new ThemeSettings(root).Save(true);
            Assert.True(settings.Save(["Gemini", "站点 · key-id", "DSH Desktop"]));
            Assert.Equal(new[] { "DSH Desktop", "Gemini", "站点 · key-id" }, new ClientVisibilitySettings(root).Load().Order(StringComparer.Ordinal));
            Assert.Equal(TokenUnit.International, new TokenUnitSettings(root).Load());
            Assert.True(new ThemeSettings(root).IsLight);
            foreach (var value in new[] { "broken", "null", "{}", "{\"HiddenClients\":null}", "[]" })
            {
                File.WriteAllText(Path.Combine(root, "client-visibility.json"), value);
                Assert.Empty(settings.Load());
            }
            File.WriteAllText(Path.Combine(root, "client-visibility.json"), "{\"HiddenClients\":[null,\"\",\"Codex\",\"Codex\"]}");
            Assert.Equal("Codex", Assert.Single(settings.Load()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("WorkBuddy.exe", "", true, false)]
    [InlineData("WorkBuddy AI.exe", "", true, false)]
    [InlineData("DSH Desktop.exe", "", false, true)]
    [InlineData("Other.exe", "C:\\Apps\\WorkBuddy.exe", true, false)]
    [InlineData("Other.exe", "D:\\DSH\\DSH Desktop\\DSH Desktop.exe", false, true)]
    [InlineData("node.exe", "C:\\Other.exe", false, false)]
    public void ClientDetectionUsesExecutableNotIncidentalCommandText(string name, string path, bool buddy, bool dsh)
    {
        var process = new ProcessSnapshot(1, 0, name, "echo WorkBuddy.exe DSH Desktop.exe", path, 0, null, 1);
        Assert.Equal(buddy, WindowsSystemScanner.IsWorkBuddy(process));
        Assert.Equal(dsh, WindowsSystemScanner.IsDsh(process));
    }
}
