using VibeGauge.Proxy.Usage;

namespace VibeGauge.Proxy.Streaming;

public sealed class StreamingForwarder
{
    public async Task ForwardAsync(
        Stream upstream,
        Stream downstream,
        UsageAccumulator usage,
        CancellationToken clientCancellation,
        CancellationToken upstreamCancellation,
        bool ndjson = false)
    {
        var parser = new SseParser(usage, ndjson);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await upstream.ReadAsync(buffer, upstreamCancellation);
            if (read == 0) break;
            parser.Push(buffer.AsSpan(0, read));
            try
            {
                await downstream.WriteAsync(buffer.AsMemory(0, read), clientCancellation);
                await downstream.FlushAsync(clientCancellation);
            }
            catch (OperationCanceledException) when (clientCancellation.IsCancellationRequested) { break; }
            catch (IOException) { break; }
        }
        parser.Complete();
    }
}
