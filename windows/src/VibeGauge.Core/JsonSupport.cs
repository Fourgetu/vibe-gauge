using System.Text;
using System.Text.Json;

namespace VibeGauge.Core;

internal static class JsonSupport
{
    public static JsonDocument? ReadDocument(string path)
    {
        try
        {
            return File.Exists(path) ? JsonDocument.Parse(File.ReadAllBytes(path)) : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static IEnumerable<string> ReadLinesShared(string path)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            stream = null;
            while (reader.ReadLine() is { } line) yield return line;
        }
        finally { stream?.Dispose(); }
    }

    public static JsonElement? FindObjectWithProperty(JsonElement root, string property)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(property, out _)) return root;
            foreach (var child in root.EnumerateObject())
                if (FindObjectWithProperty(child.Value, property) is { } found) return found;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in root.EnumerateArray())
                if (FindObjectWithProperty(child, property) is { } found) return found;
        }
        return null;
    }

    public static long Long(this JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item)) return 0;
        return item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) ? number : 0;
    }

    public static double? DoubleOrNull(this JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.Number) return null;
        return item.TryGetDouble(out var number) ? number : null;
    }

    public static string StringOrEmpty(this JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? ""
            : "";
}
