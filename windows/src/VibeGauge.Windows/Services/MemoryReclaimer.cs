using System.Runtime;
using System.Runtime.InteropServices;

namespace VibeGauge.Windows.Services;

internal static class MemoryReclaimer
{
    private static int inFlight;

    public static void CompactManagedHeapAsync()
    {
        _ = Task.Run(() =>
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
            }
            catch { }
        });
    }

    public static void ReclaimHiddenMemory(Func<bool> isStillHidden)
    {
        if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!isStillHidden()) return;
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                if (!isStillHidden()) return;
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                await Task.Delay(150).ConfigureAwait(false);
                if (!isStillHidden()) return;
                var current = GetCurrentProcess();
                if (!K32EmptyWorkingSet(current))
                    SetProcessWorkingSetSize(current, new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
            finally { Interlocked.Exchange(ref inFlight, 0); }
        });
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);
}
