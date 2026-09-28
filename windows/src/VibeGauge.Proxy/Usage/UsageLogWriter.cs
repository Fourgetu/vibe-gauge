using System.Text.Json;

namespace VibeGauge.Proxy.Usage;

public sealed class UsageLogWriter(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task AppendAsync(UsageRecord record, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("usage log has no directory");
        Directory.CreateDirectory(directory);
        var line = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(line, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }
}
