namespace VibeGauge.Windows.Services;

// Owned by the UI thread. A scope change invalidates in-flight results; ticks never queue.
internal sealed class VisibleRefreshLoop<T>(Func<Task<T>> scan, Action<T> apply) : IDisposable
{
    private long generation;
    private bool running, enabled, disposed;
    public void SetEnabled(bool value) { if (enabled != value) { enabled = value; Invalidate(); } }
    public void Invalidate() => generation++;
    public async Task TickAsync()
    {
        if (!enabled || running || disposed) return;
        running = true;
        var started = generation;
        try
        {
            var result = await scan();
            if (enabled && !disposed && generation == started) apply(result);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Keep the last valid observation. */ }
        finally { running = false; }
    }
    public void Dispose() { disposed = true; enabled = false; Invalidate(); }
}

internal sealed record LightweightSnapshot(VibeGauge.Core.ProcessReport? Activity,
    IReadOnlyList<VibeGauge.Core.NetworkAdapterInfo>? Network);
