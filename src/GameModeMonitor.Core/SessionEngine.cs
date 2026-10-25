using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameModeMonitor.Core;

public enum TargetKind { Service, App, Docker, Wsl }
public enum Outcome { Pending, Stopped, PartialStopped, Failed, Restored, RestoreFailed, NeedsReview }
public record Target(string Key, TargetKind Kind, string Label, long MemoryBytes);
public record ProcessIdentity(int Pid, long StartTicks);
public record TargetSnapshot(Target Target, List<ProcessIdentity> Instances);
public record ActionResult(bool Changed, bool Complete, string Message);
public sealed class SessionEntry
{
    public required TargetSnapshot Target { get; init; }
    public Outcome Outcome { get; set; }
    public string Message { get; set; } = "";
}
public sealed class SessionState
{
    public int Version { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset BootUtc { get; init; }
    public List<SessionEntry> Entries { get; init; } = [];
}
public interface IStateStore
{
    SessionState? Load();
    void Save(SessionState state);
    void Archive();
}
public interface IGameBackend
{
    Task<TargetSnapshot?> CaptureAsync(Target target);
    Task<bool> IsActiveAsync(Target target);
    Task<ActionResult> StopAsync(TargetSnapshot target);
    Task StartAsync(Target target);
}

public sealed class SessionEngine
{
    private readonly IGameBackend backend;
    private readonly IStateStore store;
    private readonly DateTimeOffset boot;
    private readonly SemaphoreSlim operation = new(1, 1);
    public SessionState? State { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsPreviousBoot => State is not null && Math.Abs((State.BootUtc - boot).TotalSeconds) > 10;
    public bool CanArchive => !IsBusy && State is not null;
    public bool NeedsRestart => State?.Entries.Any(e => e.Outcome is Outcome.RestoreFailed or Outcome.NeedsReview or Outcome.Pending) == true;

    public SessionEngine(IGameBackend backend, IStateStore store, DateTimeOffset boot)
    {
        this.backend = backend; this.store = store; this.boot = boot;
        State = store.Load();
        if (State is { Version: not 1 }) throw new IOException("Versión del registro no compatible.");
        if (State is not null)
            foreach (var entry in State.Entries.Where(e => e.Outcome == Outcome.Pending))
            {
                entry.Outcome = Outcome.NeedsReview;
                entry.Message = "Operación interrumpida: compruebe el estado manualmente; no se repetirá.";
            }
    }

    public string GetStatus()
    {
        if (State is null) return "Normal";
        if (IsPreviousBoot || State.Entries.Any(e => e.Outcome is Outcome.Pending or Outcome.Failed or Outcome.PartialStopped or Outcome.RestoreFailed or Outcome.NeedsReview))
            return "Recuperación parcial";
        return State.Entries.Any(e => e.Outcome == Outcome.Stopped) ? "Modo juego" : "Normal";
    }

    public async Task OptimizeAsync(IReadOnlyList<Target> selection, bool virtualizationConfirmed)
    {
        if (!await operation.WaitAsync(0)) return;
        try
        {
            IsBusy = true;
            // A journal is explicitly archived before another session may begin.
            if (State is not null || selection.Count == 0) return;
            var targets = selection.DistinctBy(t => t.Key).Where(t => virtualizationConfirmed || t.Kind is not (TargetKind.Docker or TargetKind.Wsl))
                .OrderBy(t => t.Kind == TargetKind.Wsl ? 2 : t.Kind == TargetKind.Docker ? 1 : 0).ToArray();
            if (targets.Length == 0) return;
            State = new() { BootUtc = boot };
            store.Save(State);
            var dockerSafe = true;
            foreach (var target in targets)
            {
                if (target.Kind == TargetKind.Wsl && !dockerSafe)
                {
                    State.Entries.Add(new() { Target = new(target, []), Outcome = Outcome.Failed, Message = "WSL omitido: Docker no se cerró correctamente." });
                    store.Save(State); continue;
                }
                SessionEntry? entry = null;
                try
                {
                    var snapshot = await backend.CaptureAsync(target);
                    if (snapshot is null) continue;
                    entry = new() { Target = snapshot, Outcome = Outcome.Pending, Message = "Cierre iniciado." };
                    State.Entries.Add(entry);
                    store.Save(State); // Durable intent before every side effect.
                    var result = await backend.StopAsync(snapshot);
                    entry.Outcome = result.Changed ? (result.Complete ? Outcome.Stopped : Outcome.PartialStopped) : Outcome.Failed;
                    entry.Message = result.Message;
                    if (target.Kind == TargetKind.Docker) dockerSafe = result.Complete;
                }
                catch (Exception ex)
                {
                    entry ??= new() { Target = new(target, []), Outcome = Outcome.Failed };
                    if (!State.Entries.Contains(entry)) State.Entries.Add(entry);
                    entry.Outcome = Outcome.Failed; entry.Message = ex.Message;
                    if (target.Kind == TargetKind.Docker) dockerSafe = false;
                }
                store.Save(State);
            }
        }
        finally { IsBusy = false; operation.Release(); }
    }

    public async Task RestoreAsync()
    {
        if (!await operation.WaitAsync(0)) return;
        try
        {
            IsBusy = true;
            if (State is null || IsPreviousBoot || State.Entries.Any(e => e.Outcome is Outcome.Pending or Outcome.NeedsReview)) return;
            foreach (var entry in State.Entries.Where(e => e.Outcome is Outcome.Stopped or Outcome.PartialStopped or Outcome.RestoreFailed))
            {
                var target = entry.Target.Target;
                if (target.Kind == TargetKind.Wsl)
                {
                    entry.Outcome = Outcome.Restored;
                    entry.Message = "WSL se iniciará al abrir su distribución. Las sesiones no se reconstruyen.";
                    store.Save(State); continue;
                }
                try
                {
                    if (!await backend.IsActiveAsync(target))
                    {
                        entry.Outcome = Outcome.Pending;
                        entry.Message = "Restauración iniciada; un cierre inesperado requerirá revisión manual.";
                        store.Save(State); // Durable restoration intent before the helper/app can outlive this monitor.
                        await backend.StartAsync(target);
                    }
                    entry.Outcome = Outcome.Restored;
                    entry.Message = "Servicio recuperado o aplicación abierta. No se reconstruyen proyectos ni terminales.";
                }
                catch (Exception ex) { entry.Outcome = Outcome.RestoreFailed; entry.Message = ex.Message; }
                store.Save(State);
            }
        }
        finally { IsBusy = false; operation.Release(); }
    }

    public void Archive()
    {
        if (!operation.Wait(0)) return;
        try { store.Archive(); State = null; }
        finally { operation.Release(); }
    }

    public async Task ReconcileAfterBootAsync()
    {
        if (!await operation.WaitAsync(0)) return;
        try
        {
            IsBusy = true;
            if (State is null || !IsPreviousBoot) return;
            foreach (var entry in State.Entries)
            {
                if (entry.Target.Target.Kind == TargetKind.Service)
                {
                    try
                    {
                        var recovered = await backend.IsActiveAsync(entry.Target.Target);
                        entry.Outcome = recovered ? Outcome.Restored : Outcome.RestoreFailed;
                        entry.Message = recovered ? "Windows recuperó el servicio al arrancar." : "Windows no confirmó el servicio activo. Revise Servicios o reinicie; no se ejecutó ninguna orden al iniciar sesión.";
                    }
                    catch (Exception ex) { entry.Outcome = Outcome.RestoreFailed; entry.Message = ex.Message; }
                }
                else
                {
                    entry.Outcome = Outcome.Restored;
                    entry.Message = "El reinicio terminó la sesión temporal. Abra aplicaciones, contenedores y terminales según su uso habitual.";
                }
            }
            store.Save(State);
            if (State.Entries.All(e => e.Outcome == Outcome.Restored)) { store.Archive(); State = null; }
        }
        finally { IsBusy = false; operation.Release(); }
    }
}

public sealed class JsonStateStore(string directory) : IStateStore
{
    private string FilePath => Path.Combine(directory, "session.json");
    public SessionState? Load()
    {
        if (!File.Exists(FilePath)) return null;
        if (new FileInfo(FilePath).Length > 1_000_000) throw new IOException("Registro demasiado grande; revise session.json manualmente.");
        try { return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(FilePath)) ?? throw new IOException("Registro vacío."); }
        catch (JsonException ex) { throw new IOException("Registro dañado. Se conserva sin ejecutar acciones.", ex); }
    }
    public void Save(SessionState state)
    {
        AtomicJson.Write(FilePath, state);
    }
    public void Archive()
    {
        if (File.Exists(FilePath)) File.Move(FilePath, Path.Combine(directory, $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
    }
}

public static class ServicePolicy
{
    public static bool IsWindowsName(string name) => name is "WSearch" or "Spooler" or "DiagTrack" or "SysMain";
    public static bool IsKnownName(string name) => IsWindowsName(name) || Regex.IsMatch(name, @"\A(?:MySQL\d{0,3}|MariaDB|postgresql-x64-\d{1,2}|MongoDB|Redis|MSSQLSERVER|MSSQL\$[A-Za-z0-9_]{1,32})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool AllowsWindowsService(string name, string executable, string windowsDirectory)
    {
        var relative = name switch { "WSearch" => @"System32\SearchIndexer.exe", "Spooler" => @"System32\spoolsv.exe", "DiagTrack" or "SysMain" => @"System32\svchost.exe", _ => null };
        return relative is not null && executable.Equals(windowsDirectory.TrimEnd('\\') + "\\" + relative, StringComparison.OrdinalIgnoreCase);
    }
    public static bool Allows(string name, string executable, string programFiles)
    {
        if (!IsKnownName(name) || IsWindowsName(name) || executable.Contains("..") || executable.Contains('"')) return false;
        var root = programFiles.TrimEnd('\\', '/') + "\\";
        if (!executable.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var file = executable.Replace('/', '\\').Split('\\').Last();
        return name.StartsWith("MySQL", StringComparison.OrdinalIgnoreCase) || name.Equals("MariaDB", StringComparison.OrdinalIgnoreCase)
            ? file.Equals("mysqld.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("mariadbd.exe", StringComparison.OrdinalIgnoreCase)
            : name.StartsWith("postgresql", StringComparison.OrdinalIgnoreCase) ? file.Equals("pg_ctl.exe", StringComparison.OrdinalIgnoreCase)
            : name.Equals("MongoDB", StringComparison.OrdinalIgnoreCase) ? file.Equals("mongod.exe", StringComparison.OrdinalIgnoreCase)
            : name.Equals("Redis", StringComparison.OrdinalIgnoreCase) ? file.Equals("redis-server.exe", StringComparison.OrdinalIgnoreCase)
            : file.Equals("sqlservr.exe", StringComparison.OrdinalIgnoreCase);
    }
}
