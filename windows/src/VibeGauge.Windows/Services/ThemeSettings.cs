using System.IO;
using System.Text.Json;

namespace VibeGauge.Windows.Services;

public sealed class ThemeSettings(string directory)
{
    private readonly string file = Path.Combine(directory, "appearance.json");
    public bool IsLight
    {
        get
        {
            try { return JsonSerializer.Deserialize<Preference>(File.ReadAllText(file))?.Theme == "light"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }
    }
    public void Save(bool light)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new Preference(light ? "light" : "dark")));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private sealed record Preference(string Theme);
}
