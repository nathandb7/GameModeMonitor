using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using GameModeMonitor.Core;

namespace GameModeMonitor.Windows;

public sealed class WindowsBackend(ICommandRunner commands, Action<string>? slowOperation = null) : IGameBackend
{
    private readonly string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
    private readonly string docker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe");
    private static readonly string[] LocalDocker = ["--host", "npipe:////./pipe/docker_engine"];
    private Dictionary<string, string> Catalog => AppCatalog.Resolve();

    private string AppPath(Target target)
    {
        if (target.Kind is not (TargetKind.App or TargetKind.Docker) || !Catalog.TryGetValue(target.Key, out var path)
            || (target.Kind == TargetKind.Docker) != (target.Key == "docker")) throw new UnauthorizedAccessException("Aplicación fuera del catálogo.");
        return path;
    }
    private static string ServiceName(Target target)
    {
        if (target.Kind != TargetKind.Service || !target.Key.StartsWith("service:", StringComparison.Ordinal)
            || !ServicePolicy.IsKnownName(target.Key[8..])) throw new UnauthorizedAccessException("Servicio fuera del catálogo.");
        return target.Key[8..];
    }
    public IReadOnlyList<Target> Discover(IReadOnlyList<ProcessSample> processes)
    {
        var targets = new List<Target>();
        foreach (var service in NativeServices.Discover().Where(s => s.Running && s.Automatic))
            targets.Add(new("service:" + service.Name, TargetKind.Service, service.Name + " · servicio", 0));
        foreach (var (key, path) in Catalog)
        {
            var tree = ProcessInventory.Tree(processes, path);
            if (tree.Count != 0) targets.Add(new(key, key == "docker" ? TargetKind.Docker : TargetKind.App,
                Path.GetFileNameWithoutExtension(path) + " · " + tree.Count + " procesos", tree.Sum(p => p.MemoryBytes)));
        }
        if (File.Exists(wsl) && processes.Any(p => p.Name.Equals("wslhost", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("vmmemWSL", StringComparison.OrdinalIgnoreCase)))
            targets.Add(new("wsl", TargetKind.Wsl, "WSL · todas las distribuciones", processes.Where(p => p.Name.Equals("vmmemWSL", StringComparison.OrdinalIgnoreCase)).Sum(p => p.MemoryBytes)));
        return targets;
    }
    public async Task<TargetSnapshot?> CaptureAsync(Target target)
    {
        if (target.Kind == TargetKind.Service && !NativeServices.Read(ServiceName(target)).Automatic)
            throw new IOException("El servicio no tiene inicio automático; no se pausará para conservar el arranque habitual.");
        if (!await IsActiveAsync(target)) return null;
        var instances = target.Kind is TargetKind.App or TargetKind.Docker
            ? ProcessInventory.Tree(ProcessInventory.Read(), AppPath(target)).Select(p => new ProcessIdentity(p.Pid, p.StartTicks)).ToList() : [];
        return new(target, instances);
    }
    public async Task<bool> IsActiveAsync(Target target)
    {
        if (target.Kind == TargetKind.Service) return NativeServices.Read(ServiceName(target)).Running;
        if (target.Kind == TargetKind.Wsl)
        {
            if (target.Key != "wsl") throw new UnauthorizedAccessException("Objetivo WSL no válido.");
            if (!File.Exists(wsl)) return false;
            var result = await commands.RunAsync(wsl, ["--list", "--running", "--quiet"], TimeSpan.FromSeconds(12), true);
            if (result.ExitCode != 0) throw new IOException("No se pudo consultar WSL.");
            return result.Output.Replace("\0", "").Trim().Length > 0;
        }
        var path = AppPath(target);
        return ProcessInventory.Read().Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
    }
    public async Task<ActionResult> StopAsync(TargetSnapshot snapshot)
    {
        var target = snapshot.Target;
        if (target.Kind == TargetKind.Service)
        {
            await ChangeServiceAsync("stop", ServiceName(target));
            return new(true, true, "Servicio detenido sin cambiar su inicio.");
        }
        if (target.Kind == TargetKind.Docker) return await StopDockerAsync(target);
        if (target.Kind == TargetKind.Wsl)
        {
            if (target.Key != "wsl") throw new UnauthorizedAccessException("Objetivo WSL no válido.");
            if (DockerProcessesActive()) return new(false, false, "WSL omitido: Docker sigue activo. Seleccione Docker y espere su cierre.");
            var result = await commands.RunAsync(wsl, ["--shutdown"], TimeSpan.FromSeconds(45));
            var success = result.ExitCode == 0 && !await IsActiveAsync(target);
            return new(success, success, success ? "WSL apagado. No se reconstruirán terminales." : "WSL no confirmó su apagado.");
        }
        return await CloseAppAsync(snapshot);
    }
    private async Task<ActionResult> CloseAppAsync(TargetSnapshot snapshot)
    {
        var executable = AppPath(snapshot.Target);
        var current = ProcessInventory.Read();
        var roots = current.Where(p => p.Path.Equals(executable, StringComparison.OrdinalIgnoreCase)
            && snapshot.Instances.Any(i => i.Pid == p.Pid && i.StartTicks == p.StartTicks) && p.HasWindow).ToArray();
        if (roots.Length == 0) return new(false, false, "Sin ventana cerrable: cierre la aplicación manualmente.");
        foreach (var root in roots)
        {
            try
            {
                using var process = Process.GetProcessById(root.Pid);
                if (process.StartTime.ToUniversalTime().Ticks == root.StartTicks) process.CloseMainWindow();
            }
            catch (ArgumentException) { }
        }
        var end = DateTime.UtcNow.AddSeconds(15);
        List<ProcessSample> remaining;
        do
        {
            await Task.Delay(300);
            remaining = ProcessInventory.Read().Where(p => snapshot.Instances.Any(i => i.Pid == p.Pid && i.StartTicks == p.StartTicks)).ToList();
            if (remaining.Count == 0) return new(true, true, "Aplicación cerrada normalmente.");
        } while (DateTime.UtcNow < end);
        return new(remaining.Count < snapshot.Instances.Count, false, "Algunos procesos siguen activos. Guarde su trabajo o cierre la aplicación manualmente; no se forzó nada.");
    }
    private bool DockerProcessesActive()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker") + Path.DirectorySeparatorChar;
        return ProcessInventory.Read().Any(p => p.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }
    private async Task<CommandResult> DockerAsync(params string[] arguments)
    {
        if (!File.Exists(docker)) throw new IOException("CLI local de Docker no disponible. No se apagará WSL.");
        var result = await commands.RunAsync(docker, [.. LocalDocker, .. arguments], TimeSpan.FromSeconds(60));
        if (result.ExitCode != 0) throw new IOException("Docker no completó la operación local. Revise Docker Desktop; no se apagará WSL.");
        return result;
    }
    private async Task<ActionResult> StopDockerAsync(Target target)
    {
        _ = AppPath(target);
        var list = await DockerAsync("ps", "--quiet", "--no-trunc");
        var ids = list.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Any(id => !Regex.IsMatch(id, "\\A[a-f0-9]{64}\\z"))) throw new IOException("Respuesta de Docker inesperada.");
        foreach (var id in ids)
        {
            // Infinite grace period: Docker must never escalate to SIGKILL.
            await DockerAsync("container", "stop", "--timeout", "-1", id);
        }
        if ((await DockerAsync("ps", "--quiet")).Output.Trim().Length != 0)
            return new(false, false, "Hay contenedores activos; no se cerró Docker ni WSL.");
        await DockerAsync("desktop", "stop", "--timeout", "45");
        var end = DateTime.UtcNow.AddSeconds(15);
        while (DockerProcessesActive() && DateTime.UtcNow < end) await Task.Delay(500);
        var closed = !DockerProcessesActive();
        return new(closed, closed, closed ? "Docker cerrado; los contenedores no se iniciarán explícitamente al revertir." : "Docker mantiene procesos activos; WSL no se apagará.");
    }
    private async Task ChangeServiceAsync(string action, string name)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "GameModeMonitor.ServiceHelper.exe");
        if (!File.Exists(helper)) throw new IOException("Falta el auxiliar de servicios junto a la aplicación.");
        var info = new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add(action); info.ArgumentList.Add(name);
        using var process = Process.Start(info) ?? throw new IOException("No se pudo abrir el auxiliar.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            slowOperation?.Invoke("El auxiliar sigue en curso. Se espera su salida sin habilitar otra operación.");
            await process.WaitForExitAsync();
            throw new TimeoutException("El auxiliar terminó fuera del tiempo previsto. Compruebe el servicio antes de continuar.");
        }
        if (process.ExitCode != 0) throw new IOException(process.ExitCode switch
        {
            3 => "Permisos insuficientes o servicio fuera del catálogo protegido.",
            5 => "Tiempo agotado: compruebe el estado del servicio manualmente.",
            _ => "No se cambió el servicio o no se confirmó el resultado. Puede tener dependencias activas."
        });
    }
    public async Task StartAsync(Target target)
    {
        if (target.Kind == TargetKind.Service)
        {
            await ChangeServiceAsync("start", ServiceName(target));
            if (!await IsActiveAsync(target)) throw new IOException("El servicio no confirmó el inicio.");
            return;
        }
        if (target.Kind == TargetKind.Wsl) throw new UnauthorizedAccessException("Las sesiones WSL no se reconstruyen.");
        var path = AppPath(target); // Resolve catalog key afresh; never execute a journal path or arguments.
        if (await IsActiveAsync(target)) return;
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! });
        if (process is null) throw new IOException("La aplicación no se pudo abrir.");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsActiveAsync(target)) return;
            await Task.Delay(500);
        }
        throw new IOException("Se solicitó abrir la aplicación, pero no confirmó su inicio.");
    }
}
