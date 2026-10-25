using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameModeMonitor.Windows;

public record MemoryCleanupResult(long AvailableBefore, long AvailableAfter, long OwnWorkingSetBefore, long OwnWorkingSetAfter, bool TrimSucceeded)
{
    public double AvailableDeltaMiB => (AvailableAfter - AvailableBefore) / 1048576.0;
    public double OwnReductionMiB => Math.Max(0, OwnWorkingSetBefore - OwnWorkingSetAfter) / 1048576.0;
}

public static class MemoryCleaner
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool K32EmptyWorkingSet(IntPtr process);
    // Deliberately accepts no PID or process handle: never trim a game or another app.
    public static async Task<MemoryCleanupResult> CleanAsync()
    {
        var monitor = new ResourceMonitor();
        var before = monitor.Read();
        using var own = Process.GetCurrentProcess();
        own.Refresh(); var ownBefore = own.WorkingSet64;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false, false);
        var success = ownBefore >= 32 * 1048576 && K32EmptyWorkingSet(own.Handle);
        await Task.Delay(500);
        own.Refresh(); var after = monitor.Read();
        return new((long)before.AvailableMemory, (long)after.AvailableMemory, ownBefore, own.WorkingSet64, success);
    }
}
