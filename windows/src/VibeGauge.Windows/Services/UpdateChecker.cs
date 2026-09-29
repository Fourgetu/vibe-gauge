using System.IO;
using System.Net.Http;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class UpdateChecker(AppPaths paths)
{
    private DateTimeOffset? attempted;
    private Task<UpdateRelease?>? pending;
    private UpdateRelease? latest;
    public string Status { get; private set; } = "尚未检查新版";
    private string StateFile => Path.Combine(paths.LocalDataRoot, "update-state.json");
    private sealed record State(DateTimeOffset CheckedAt, string Notified);
    private State Read() { try { return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new(default, ""); } catch { return new(default, ""); } }
    private void Save(State state)
    {
        try { Directory.CreateDirectory(paths.LocalDataRoot); File.WriteAllText(StateFile + ".tmp", JsonSerializer.Serialize(state)); File.Move(StateFile + ".tmp", StateFile, true); }
        catch (IOException) { }
    }
    public UpdateRelease? Scan()
    {
        if (pending is { IsCompleted: true }) { if (pending.IsCompletedSuccessfully) latest = pending.Result; pending = null; }
        if (FeaturePreferences.Load(paths).CheckUpdates && pending is null && DateTimeOffset.Now - (attempted ?? Read().CheckedAt) >= TimeSpan.FromDays(1)) pending = CheckAsync();
        return latest;
    }
    public async Task<UpdateRelease?> CheckAsync()
    {
        attempted = DateTimeOffset.Now;
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VibeGauge-Windows/1.0");
        try
        {
            using var response = await client.GetAsync("https://api.github.com/repos/Fourgetu/vibe-gauge/releases/latest");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var version = typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            latest = UpdateRelease.Parse(doc.RootElement, version);
            Status = latest is null ? "未发现适用的 Windows 正式新版" : $"发现 {latest.Version} · {latest.Asset}";
            Save(Read() with { CheckedAt = attempted.Value });
            return latest;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { Status = "检查失败，请稍后重试"; return null; }
    }
    public bool TakeNotification(UpdateRelease release)
    {
        var state = Read();
        if (state.Notified == release.Version) return false;
        Save(state with { Notified = release.Version }); return true;
    }
}
