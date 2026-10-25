using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameModeMonitor.Windows;

public record ProcessSample(int Pid, int ParentPid, long StartTicks, string Name, string Path, long MemoryBytes, bool HasWindow);
public record ResourceSample(double CpuPercent, ulong TotalMemory, ulong AvailableMemory);

public static class ProcessInventory
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr Heap;
        public uint Module, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    public static List<ProcessSample> Read()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot != new IntPtr(-1))
        {
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
                if (Process32First(snapshot, ref entry)) do { parents[(int)entry.Pid] = (int)entry.ParentPid; } while (Process32Next(snapshot, ref entry));
            }
            finally { CloseHandle(snapshot); }
        }
        var result = new List<ProcessSample>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName ?? "";
                    result.Add(new(process.Id, parents.GetValueOrDefault(process.Id), process.StartTime.ToUniversalTime().Ticks,
                        process.ProcessName, path, process.WorkingSet64, process.MainWindowHandle != IntPtr.Zero));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return result;
    }
    public static IReadOnlyList<ProcessSample> Tree(IReadOnlyList<ProcessSample> all, string executable)
    {
        var selected = all.Where(p => p.Path.Equals(executable, StringComparison.OrdinalIgnoreCase)).ToList();
        var ids = selected.Select(p => p.Pid).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var child in all.Where(p => ids.Contains(p.ParentPid) && !ids.Contains(p.Pid)).ToArray())
            {
                var parent = selected.First(p => p.Pid == child.ParentPid);
                if (child.StartTicks < parent.StartTicks) continue; // Reused parent PID.
                ids.Add(child.Pid); selected.Add(child); changed = true;
            }
        } while (changed);
        return selected;
    }
    public static bool IsDevelopment(ProcessSample p) =>
        new[] { "node", "node_repl", "python", "pythonw", "php", "java", "javaw", "dotnet", "code", "devenv", "idea64", "rider64", "pycharm64", "webstorm64", "opencode", "opencode-cli", "codex", "mysqld", "postgres", "mongod", "redis-server", "docker desktop", "com.docker.backend", "vmmemwsl", "wslhost" }
        .Contains(p.Name, StringComparer.OrdinalIgnoreCase);
}

public sealed class ResourceMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    private long lastIdle, lastTotal;
    public ResourceSample Read()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new IOException("No se pudo leer la CPU.");
        var total = kernel + user;
        var delta = total - lastTotal;
        var cpu = lastTotal == 0 || delta <= 0 ? 0 : Math.Clamp(100.0 * (delta - (idle - lastIdle)) / delta, 0, 100);
        lastIdle = idle; lastTotal = total;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new IOException("No se pudo leer la memoria.");
        return new(cpu, memory.TotalPhysical, memory.AvailablePhysical);
    }
}
