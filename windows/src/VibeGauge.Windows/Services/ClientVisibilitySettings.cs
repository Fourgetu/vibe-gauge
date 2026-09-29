using System.IO;
using System.Text.Json;

namespace VibeGauge.Windows.Services;

public sealed class ClientVisibilitySettings(string directory)
{
    private readonly string file = Path.Combine(directory, "client-visibility.json");
    public HashSet<string> Load()
    {
        try
        {
            var names = JsonSerializer.Deserialize<Preference>(File.ReadAllText(file))?.HiddenClients ?? [];
            return new(names.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(StringComparer.Ordinal); }
    }

    public bool Save(IEnumerable<string> hidden)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new Preference(hidden.Order(StringComparer.Ordinal).ToArray())));
            File.Move(file + ".tmp", file, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private sealed record Preference(string[] HiddenClients);
}
