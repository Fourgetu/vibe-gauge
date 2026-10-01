using System.IO;
using System.Text.Json;

namespace VibeGauge.Windows.Services;

public sealed class ThemeSettings(string directory)
{
    private readonly string file = Path.Combine(directory, "appearance.json");
    public bool IsLight
    {
        get => Load().Theme == "light";
    }
    public int GetTransparency(bool light)
    {
        var preference = Load();
        return Math.Clamp((light ? preference.LightTransparency : preference.DarkTransparency)
            ?? (light ? ThemePalette.DefaultLightTransparency : ThemePalette.DefaultDarkTransparency), 0, 100);
    }
    public void Save(bool light) => Write(Load() with { Theme = light ? "light" : "dark" });
    public void SaveTransparency(bool light, int value)
    {
        var preference = Load();
        value = Math.Clamp(value, 0, 100);
        Write(light ? preference with { LightTransparency = value } : preference with { DarkTransparency = value });
    }
    private Preference Load()
    {
        try { return JsonSerializer.Deserialize<Preference>(File.ReadAllText(file)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    private void Write(Preference preference)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(preference));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private sealed record Preference(string Theme = "dark", int? LightTransparency = null, int? DarkTransparency = null);
}
