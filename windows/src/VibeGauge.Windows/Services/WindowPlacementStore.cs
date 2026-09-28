using System.IO;
using System.Text.Json;

namespace VibeGauge.Windows.Services;

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Pinned);

public sealed class WindowPlacementStore(string directory)
{
    private readonly string file = Path.Combine(directory, "window-placement.json");
    public WindowPlacement? Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(file));
            return value is not null && double.IsFinite(value.Left) && double.IsFinite(value.Top) &&
                value.Width is >= 420 and <= 4000 && value.Height is >= 420 and <= 4000 ? value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(WindowPlacement value)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(value));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
