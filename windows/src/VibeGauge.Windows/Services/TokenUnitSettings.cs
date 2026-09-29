using System.IO;
using System.Text.Json;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class TokenUnitSettings(string directory)
{
    private readonly string file = Path.Combine(directory, "token-display.json");

    public TokenUnit Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Preference>(File.ReadAllText(file))?.Unit == "international"
                ? TokenUnit.International : TokenUnit.Chinese;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return TokenUnit.Chinese; }
    }

    public bool Save(TokenUnit unit)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new Preference(unit == TokenUnit.International ? "international" : "chinese")));
            File.Move(file + ".tmp", file, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private sealed record Preference(string Unit);
}
