using System.ComponentModel;
using System.Runtime.InteropServices;
using GameModeMonitor.Core;
using Microsoft.Win32;

namespace GameModeMonitor.Windows;

public record ServiceInfo(string Name, string Executable, bool Running, uint State, bool Automatic);

public static class NativeServices
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, State, Controls, Win32Exit, SpecificExit, Checkpoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        public uint Type, StartType, ErrorControl;
        public IntPtr BinaryPath, LoadGroup;
        public uint Tag;
        public IntPtr Dependencies, Account, DisplayName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(IntPtr service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EnumDependentServices(IntPtr service, uint state, IntPtr buffer, uint size, out uint needed, out uint count);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartService(IntPtr service, uint count, IntPtr arguments);

    private static T WithService<T>(string name, uint access, Func<IntPtr, T> action)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, name, access);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return action(service); }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    private static uint Status(IntPtr service)
    {
        if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return status.State;
    }
    private static (string Path, uint StartType) Binary(IntPtr service)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out var size);
        if (size == 0 || size > 65536) throw new IOException("Configuración de servicio no válida.");
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!QueryServiceConfig(service, buffer, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
            if ((config.Type & 0x30) is not (0x10 or 0x20)) throw new IOException("Tipo de servicio no permitido.");
            var command = Environment.ExpandEnvironmentVariables(Marshal.PtrToStringUni(config.BinaryPath) ?? "").Trim();
            // Ambiguous, unquoted paths are refused rather than parsed as commands.
            if (command.StartsWith('"'))
            {
                var end = command.IndexOf('"', 1);
                if (end < 2) throw new IOException("Ruta de servicio no válida.");
                return (command[1..end], config.StartType);
            }
            var token = command.Split(' ', 2)[0];
            if (!token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("La ruta del servicio debe ser inequívoca.");
            return (token, config.StartType);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static bool Trusted(string name, string path)
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var allowed = ServicePolicy.IsWindowsName(name) ? ServicePolicy.AllowsWindowsService(name, path, windows) : roots.Any(root => ServicePolicy.Allows(name, path, root));
        if (!allowed) return false;
        if (name is "SysMain" or "DiagTrack")
        {
            using var parameters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name + @"\Parameters");
            var dll = Environment.ExpandEnvironmentVariables(parameters?.GetValue("ServiceDll") as string ?? "");
            var expected = Path.Combine(windows, "System32", name == "SysMain" ? "sysmain.dll" : "diagtrack.dll");
            if (!dll.Equals(expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(dll) || HasReparsePoint(dll)) return false;
        }
        // Do not trust junctions/symlinks that escape the protected install tree.
        return File.Exists(path) && !HasReparsePoint(path);
    }
    private static bool HasReparsePoint(string path)
    {
        for (var item = new FileInfo(path) as FileSystemInfo; item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
    public static ServiceInfo Read(string name) => WithService(name, 1 | 4, handle =>
    {
        var config = Binary(handle);
        if (!Trusted(name, config.Path)) throw new UnauthorizedAccessException("Servicio fuera del catálogo o de una instalación protegida.");
        var state = Status(handle);
        return new ServiceInfo(name, config.Path, state == 4, state, config.StartType == 2);
    });
    public static IReadOnlyList<ServiceInfo> Discover()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        var result = new List<ServiceInfo>();
        foreach (var name in key?.GetSubKeyNames().Where(ServicePolicy.IsKnownName) ?? [])
            try { result.Add(Read(name)); } catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
        return result;
    }
    public static void Change(string action, string name)
    {
        if (action is not ("start" or "stop") || !ServicePolicy.IsKnownName(name)) throw new UnauthorizedAccessException("Solicitud no permitida.");
        WithService(name, 1 | 4 | 8 | (action == "stop" ? 0x20u : 0x10u), handle =>
        {
            var config = Binary(handle);
            if (!Trusted(name, config.Path)) throw new UnauthorizedAccessException("Ejecutable de servicio no permitido.");
            if (action == "stop" && config.StartType != 2) throw new IOException("Solo se pausan servicios de inicio automático, para conservar su recuperación normal al reiniciar.");
            var state = Status(handle);
            var desired = action == "stop" ? 1u : 4u;
            if (state == desired) return true;
            if (state is not (1 or 4)) throw new IOException("El servicio está cambiando de estado; inténtelo después.");
            if (action == "stop")
            {
                var success = EnumDependentServices(handle, 1, IntPtr.Zero, 0, out var needed, out var count);
                var error = Marshal.GetLastWin32Error();
                if (count != 0 || needed != 0 || (!success && error == 234)) throw new IOException("Dependencias activas: no se detuvo el servicio.");
                if (!success) throw new Win32Exception(error);
                if (!ControlService(handle, 1, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            else if (!StartService(handle, 0, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (Status(handle) == desired) return true;
                Thread.Sleep(250);
            }
            throw new TimeoutException("El servicio no confirmó el cambio. Revise su estado; no se forzó ningún proceso.");
        });
    }
}
