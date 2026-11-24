using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace VibeGauge.Windows.Services;

public sealed class ThemeSettings(string directory)
{
    private readonly string file = Path.Combine(directory, "appearance.json");

    public string ThemeMode
    {
        get
        {
            var raw = Load().Theme;
            return raw is "light" or "dark" or "system" ? raw : "dark";
        }
    }

    public string BackdropMode
    {
        get
        {
            var raw = Load().Backdrop;
            return raw is "acrylic" or "mica" or "solid" ? raw : "acrylic";
        }
    }

    public bool IsLight => ResolveIsLight(ThemeMode);

    public static bool ResolveIsLight(string mode) => mode switch
    {
        "light" => true,
        "system" => IsSystemLightTheme(),
        _ => false
    };

    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    public int GetTransparency(bool light)
    {
        var preference = Load();
        return Math.Clamp((light ? preference.LightTransparency : preference.DarkTransparency)
            ?? (light ? ThemePalette.DefaultLightTransparency : ThemePalette.DefaultDarkTransparency), 0, 100);
    }

    public void Save(bool light) => SaveThemeMode(light ? "light" : "dark");

    public void SaveThemeMode(string mode)
    {
        var normalized = mode is "light" or "dark" or "system" ? mode : "dark";
        Write(Load() with { Theme = normalized });
    }

    public void SaveBackdropMode(string mode)
    {
        var normalized = mode is "acrylic" or "mica" or "solid" ? mode : "acrylic";
        Write(Load() with { Backdrop = normalized });
    }

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

    private sealed record Preference(string Theme = "dark", int? LightTransparency = null, int? DarkTransparency = null, string Backdrop = "acrylic");
}
