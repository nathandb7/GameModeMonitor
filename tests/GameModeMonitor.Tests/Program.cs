using GameModeMonitor.Core;
using GameModeMonitor.Windows;

var passed = 0;
async Task Test(string name, Func<Task> run)
{
    await run();
    Console.WriteLine($"PASS {name}");
    passed++;
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
var boot = DateTimeOffset.UtcNow.AddHours(-1);
Target Service(string name = "MySQL84") => new("service:" + name, TargetKind.Service, name, 0);
SessionEngine Engine(FakeBackend b, MemoryStore s) => new(b, s, boot);

await Test("Only selected active targets are stopped; repeat preserves original journal", async () =>
{
    var b = new FakeBackend(); var s = new MemoryStore(); var e = Engine(b, s);
    await e.OptimizeAsync([Service()], false);
    Assert(b.Stops.SequenceEqual(["service:MySQL84"]), "Selection not respected");
    var original = s.State!.Id;
    await e.OptimizeAsync([Service("MySQL80")], false);
    Assert(s.State.Id == original && b.Stops.Count == 1, "Repeat overwrote state");
});
await Test("Empty selection and inactive targets never create recoverable actions", async () =>
{
    var b = new FakeBackend { Active = false }; var s = new MemoryStore(); var e = Engine(b, s);
    await e.OptimizeAsync([], false);
    Assert(s.State is null, "Empty selection wrote state");
    await e.OptimizeAsync([Service()], false); await e.RestoreAsync();
    Assert(b.Stops.Count == 0 && b.Starts.Count == 0, "Previously stopped service started");
});
await Test("Docker and WSL require separate explicit confirmation", async () =>
{
    var b = new FakeBackend(); var e = Engine(b, new());
    await e.OptimizeAsync([new("docker", TargetKind.Docker, "Docker", 0), new("wsl", TargetKind.Wsl, "WSL", 0)], false);
    Assert(b.Stops.Count == 0, "Unconfirmed virtualization shutdown");
});
await Test("Docker failure prevents WSL shutdown; partial result is not success", async () =>
{
    var b = new FakeBackend { FailDocker = true }; var s = new MemoryStore(); var e = Engine(b, s);
    await e.OptimizeAsync([new("wsl", TargetKind.Wsl, "WSL", 0), new("docker", TargetKind.Docker, "Docker", 0)], true);
    Assert(b.Stops.SequenceEqual(["docker"]), "WSL stopped after Docker failed");
    Assert(e.GetStatus() == "Recuperación parcial", "Failure reported as success");
});
await Test("Restore only changed targets and never rebuilds WSL sessions", async () =>
{
    var b = new FakeBackend(); var s = new MemoryStore(); var e = Engine(b, s);
    await e.OptimizeAsync([Service(), new("wsl", TargetKind.Wsl, "WSL", 0)], true);
    await e.RestoreAsync(); await e.RestoreAsync();
    Assert(b.Starts.SequenceEqual(["service:MySQL84"]), "Restore repeated or rebuilt WSL");
});
await Test("Already running app is not launched twice", async () =>
{
    var b = new FakeBackend(); var e = Engine(b, new());
    await e.OptimizeAsync([new("app:vscode", TargetKind.App, "VS Code", 0)], false);
    b.Active = true; await e.RestoreAsync();
    Assert(b.Starts.Count == 0, "Duplicate app launch");
});
await Test("Different boot refuses restore and prevents old record overwrite", async () =>
{
    var b = new FakeBackend(); var s = new MemoryStore(); var e = Engine(b, s);
    await e.OptimizeAsync([Service()], false);
    var old = s.State!.Id;
    var afterReboot = new SessionEngine(b, s, boot.AddHours(1));
    await afterReboot.RestoreAsync(); await afterReboot.OptimizeAsync([Service()], false);
    Assert(b.Starts.Count == 0 && s.State.Id == old, "Old session executed or lost");
    Assert(afterReboot.IsPreviousBoot, "Boot boundary not detected");
});
await Test("Interrupted action is retained for manual review, not replayed", async () =>
{
    var s = new MemoryStore { State = new SessionState { BootUtc = boot, Entries = [new() { Target = new(Service(), []), Outcome = Outcome.Pending }] } };
    var b = new FakeBackend(); var e = Engine(b, s);
    await e.RestoreAsync();
    Assert(b.Starts.Count == 0 && e.GetStatus() == "Recuperación parcial", "Uncertain action replayed");
});
await Test("Save failure prevents the side effect", async () =>
{
    var b = new FakeBackend(); var s = new MemoryStore { FailSave = true }; var e = Engine(b, s);
    try { await e.OptimizeAsync([Service()], false); } catch (IOException) { }
    Assert(b.Stops.Count == 0, "Changed service before durable journal");
});
await Test("Service/UAC errors and restore failure remain actionable", async () =>
{
    var b = new FakeBackend { ThrowStop = true }; var e = Engine(b, new());
    await e.OptimizeAsync([Service()], false);
    Assert(e.GetStatus() == "Recuperación parcial", "Permission failure hidden");
    b = new FakeBackend { ThrowStart = true }; var s = new MemoryStore(); e = Engine(b, s);
    await e.OptimizeAsync([Service()], false); await e.RestoreAsync();
    Assert(s.State!.Entries[0].Outcome == Outcome.RestoreFailed, "Restore failure lost");
});
await Test("Concurrent actions are rejected rather than queued", async () =>
{
    var b = new FakeBackend { Block = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var e = Engine(b, new());
    var first = e.OptimizeAsync([Service()], false);
    await b.Entered.Task;
    await e.RestoreAsync();
    Assert(b.Starts.Count == 0, "Concurrent restore ran");
    b.Block.SetResult(); await first;
});
await Test("Service policy rejects arbitrary names, system executables and paths", () =>
{
    Assert(ServicePolicy.Allows("MySQL84", @"C:\Program Files\MySQL\MySQL Server 8.4\bin\mysqld.exe", @"C:\Program Files"), "MySQL rejected");
    Assert(!ServicePolicy.Allows("WinDefend", @"C:\Program Files\MySQL\mysqld.exe", @"C:\Program Files"), "System service allowed");
    Assert(!ServicePolicy.Allows("MySQL84", @"C:\Users\someone\mysqld.exe", @"C:\Program Files"), "User binary elevated");
    Assert(!ServicePolicy.Allows("MySQL84", @"C:\Program Files\MySQL\..\Windows\cmd.exe", @"C:\Program Files"), "Traversal allowed");
    Assert(!ServicePolicy.IsKnownName("MySQL84 & calc"), "Command injection name allowed");
    return Task.CompletedTask;
});
await Test("WSL-only optimization is a game session and WSL failure is visible", async () =>
{
    var b = new FakeBackend(); var e = Engine(b, new());
    await e.OptimizeAsync([new("wsl", TargetKind.Wsl, "WSL", 0)], true);
    Assert(e.GetStatus() == "Modo juego", "WSL-only session reported normal");
    b = new FakeBackend { ThrowStop = true }; e = Engine(b, new());
    await e.OptimizeAsync([new("wsl", TargetKind.Wsl, "WSL", 0)], true);
    Assert(e.GetStatus() == "Recuperación parcial", "WSL failure hidden");
});
await Test("Partial close remains recoverable without losing failures", async () =>
{
    var b = new FakeBackend { PartialClose = true }; var s = new MemoryStore(); var e = Engine(b, s);
    await e.OptimizeAsync([new("app:vscode", TargetKind.App, "Code", 0)], false);
    Assert(s.State!.Entries[0].Outcome == Outcome.PartialStopped, "Partial closure lost");
    await e.RestoreAsync();
    Assert(b.Starts.Count == 1, "Partially closed application not recovered");
});
await Test("Atomic JSON roundtrip, archive and corrupt record fail closed", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "GameModeMonitor-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var store = new JsonStateStore(directory);
        var state = new SessionState { BootUtc = boot, Entries = [new() { Target = new(Service(), []), Outcome = Outcome.Stopped }] };
        store.Save(state);
        Assert(store.Load()!.Id == state.Id, "Atomic roundtrip failed");
        Assert(!File.Exists(Path.Combine(directory, "session.json.tmp")), "Temporary file not replaced");
        store.Archive(); Assert(store.Load() is null && Directory.GetFiles(directory).Length == 1, "Archive discarded journal");
        File.WriteAllText(Path.Combine(directory, "session.json"), "not JSON");
        try { store.Load(); throw new Exception("Corrupt journal accepted"); } catch (IOException) { }
        Assert(File.ReadAllText(Path.Combine(directory, "session.json")) == "not JSON", "Corrupt record modified");
    }
    finally { Directory.Delete(directory, true); }
    return Task.CompletedTask;
});
await Test("Widget opens as compact bar and can expand/collapse without changing session state", () =>
{
    var layout = new WidgetLayout();
    Assert(!layout.Expanded && layout.Width == 380 && layout.Height == 54, "Default is not a compact bar");
    layout.Toggle(); Assert(layout.Expanded && layout.Width == 510 && layout.Height == 790, "Expand failed");
    layout.Toggle(); Assert(!layout.Expanded && layout.Height == 54, "Collapse failed");
    layout.Expand(); layout.Expand(); Assert(layout.Expanded, "Expand is not idempotent");
    return Task.CompletedTask;
});
await Test("Timed-out command retains operation exclusion until its child has exited", async () =>
{
    var runner = new CommandRunner();
    var noticed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    runner.SlowOperation += _ => noticed.TrySetResult();
    var b = new TimeoutBackend(runner); var e = EngineForTimeout(b);
    var first = e.OptimizeAsync([Service(), new("wsl", TargetKind.Wsl, "WSL", 0)], true);
    await noticed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    Assert(e.IsBusy && !first.IsCompleted, "Timeout released operation gate");
    await e.RestoreAsync();
    Assert(b.Starts.Count == 0, "Restore overlapped child command");
    await first;
    Assert(b.ChildExited, "Gate released before child exit");
    await e.RestoreAsync();
    Assert(b.Starts.SequenceEqual(["service:MySQL84"]), "Restore did not resume after exit");
    SessionEngine EngineForTimeout(TimeoutBackend backend) => new(backend, new MemoryStore(), boot);
});
await Test("Interrupted journal blocks restoration of earlier stopped targets", async () =>
{
    var s = new MemoryStore
    {
        State = new SessionState
        {
            BootUtc = boot,
            Entries = [
        new() { Target = new(Service(), []), Outcome = Outcome.Stopped },
        new() { Target = new(new("wsl", TargetKind.Wsl, "WSL", 0), []), Outcome = Outcome.Pending }
    ]
        }
    };
    var b = new FakeBackend(); var e = Engine(b, s);
    await e.RestoreAsync();
    Assert(b.Starts.Count == 0, "Earlier target restored while interrupted command may be active");
});
await Test("Restore persists in-flight intent before starting and restart blocks another restoration", async () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "gmm-restore-" + Guid.NewGuid().ToString("N"));
    var store = new JsonStateStore(directory); var b = new FakeBackend(); var e = new SessionEngine(b, store, boot);
    await e.OptimizeAsync([Service()], false);
    b.BeforeStart = async () =>
    {
        Assert(store.Load()!.Entries[0].Outcome == Outcome.Pending, "Restore side effect preceded durable intent");
        var restarted = new SessionEngine(b, store, boot);
        Assert(restarted.State!.Entries[0].Outcome == Outcome.NeedsReview, "Interrupted restore not identified");
        await restarted.RestoreAsync();
        Assert(b.Starts.Count == 0, "Restart allowed overlapping restore");
    };
    try
    {
        await e.RestoreAsync();
        Assert(b.Starts.Count == 1 && e.State!.Entries[0].Outcome == Outcome.Restored, "Restoration did not complete");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
await Test("Failure to persist restoration intent prevents the start side effect", async () =>
{
    var store = new MemoryStore(); var b = new FakeBackend(); var e = Engine(b, store);
    await e.OptimizeAsync([Service()], false); store.FailSave = true;
    try { await e.RestoreAsync(); } catch (IOException) { }
    Assert(b.Starts.Count == 0, "Service started without durable restoration intent");
});
await Test("Profiles preselect only their declared targets and preserve editable overrides", () =>
{
    var settings = new ProfileSettings();
    Assert(settings.Profile == OptimizationProfile.Recommended, "Recommended is not default");
    Assert(settings.IsSelected("service:WSearch") && settings.IsSelected("service:Spooler"), "Recommended targets missing");
    Assert(!settings.IsSelected("service:SysMain") && !settings.IsSelected("docker") && !settings.IsSelected("wsl"), "Risky target silently selected");
    settings.SetSelected("service:WSearch", false);
    settings.Profile = OptimizationProfile.Safe;
    Assert(!settings.IsSelected("service:Spooler") && settings.IsSelected("service:MySQL84"), "Safe profile has Windows impacts");
    settings.Profile = OptimizationProfile.Experimental;
    Assert(settings.IsSelected("service:SysMain") && settings.IsSelected("service:DiagTrack"), "Experimental defaults missing");
    settings.Profile = OptimizationProfile.Recommended;
    Assert(!settings.IsSelected("service:WSearch"), "Profile-specific override lost");
    Assert(!settings.IsSelected("service:WinDefend"), "Security service preselected");
    return Task.CompletedTask;
});
await Test("Windows service trust is exact and cannot select essential service hosts", () =>
{
    Assert(ServicePolicy.AllowsWindowsService("WSearch", @"C:\Windows\System32\SearchIndexer.exe", @"C:\Windows"), "Search rejected");
    Assert(ServicePolicy.AllowsWindowsService("Spooler", @"C:\Windows\System32\spoolsv.exe", @"C:\Windows"), "Spooler rejected");
    Assert(!ServicePolicy.AllowsWindowsService("WinDefend", @"C:\Windows\System32\svchost.exe", @"C:\Windows"), "Defender allowed");
    Assert(!ServicePolicy.AllowsWindowsService("SysMain", @"C:\Users\x\svchost.exe", @"C:\Windows"), "User service host trusted");
    Assert(!ServicePolicy.AllowsWindowsService("SysMain", @"C:\Windows\System32\..\svchost.exe", @"C:\Windows"), "Traversal trusted");
    return Task.CompletedTask;
});
await Test("Postboot reconciliation verifies automatic recovery without starting any targets", async () =>
{
    var b = new FakeBackend(); var s = new MemoryStore(); var e = Engine(b, s);
    await e.OptimizeAsync([Service()], false); b.Active = true;
    var next = new SessionEngine(b, s, boot.AddHours(1));
    await next.ReconcileAfterBootAsync();
    Assert(next.State is null && b.Starts.Count == 0 && b.Stops.Count == 1, "Boot recovery replayed optimization or restoration");
});
await Test("Postboot recovery failure remains visible and requests manual recovery/restart", async () =>
{
    var b = new FakeBackend(); var s = new MemoryStore(); var e = Engine(b, s);
    await e.OptimizeAsync([Service()], false);
    var next = new SessionEngine(b, s, boot.AddHours(1)); await next.ReconcileAfterBootAsync();
    Assert(next.State is not null && next.NeedsRestart && b.Starts.Count == 0, "Failed recovery hidden or elevated at startup");
});
await Test("Profile settings roundtrip and reset preserve separate profile choices", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "gmm-profiles-" + Guid.NewGuid().ToString("N"));
    var store = new ProfileSettingsStore(directory); var settings = store.Load();
    settings.SetSelected("service:Spooler", false); settings.Profile = OptimizationProfile.Safe;
    settings.SetSelected("service:MySQL84", false); store.Save(settings);
    var loaded = store.Load();
    Assert(loaded.Profile == OptimizationProfile.Safe && !loaded.IsSelected("service:MySQL84"), "Safe override not persisted");
    loaded.ResetCurrentProfile(); Assert(loaded.IsSelected("service:MySQL84"), "Reset did not restore default");
    loaded.Profile = OptimizationProfile.Recommended; Assert(!loaded.IsSelected("service:Spooler"), "Reset erased another profile");
    File.WriteAllText(store.FilePath, "{broken");
    try { store.Load(); throw new Exception("Corrupt settings accepted"); } catch (IOException) { }
    Assert(File.ReadAllText(store.FilePath) == "{broken", "Corrupt settings overwritten");
    // Retain isolated test scratch for parent cleanup rather than deleting unknown data.
    Console.WriteLine("Scratch: " + directory);
    return Task.CompletedTask;
});
await Test("Memory cleanup reports signed observations without inventing freed system RAM", () =>
{
    var result = new MemoryCleanupResult(200 * 1048576, 190 * 1048576, 100 * 1048576, 70 * 1048576, true);
    Assert(result.AvailableDeltaMiB == -10 && result.OwnReductionMiB == 30, "Observed system delta was clamped or confused with own reduction");
    var growth = new MemoryCleanupResult(0, 0, 10, 20, false);
    Assert(growth.OwnReductionMiB == 0 && !growth.TrimSucceeded, "Failed trim claims improvement");
    return Task.CompletedTask;
});
await Test("Completed recovery offers Optimize and preserves history until the next session", async () =>
{
    var b = new FakeBackend(); var s = new MemoryStore(); var e = Engine(b, s);
    await e.OptimizeAsync([Service()], false);
    var original = e.State;
    await e.RestoreAsync();
    Assert(e.CanOptimize && !e.CanRestore, "Completed recovery still offers Revert");
    Assert(ReferenceEquals(e.State, original), "Completed history was discarded");
    var reopened = Engine(b, s);
    Assert(reopened.CanOptimize && !reopened.CanRestore, "Completed saved session blocks Optimize after reopening");
    await reopened.RestoreAsync();
    Assert(b.Starts.Count == 1, "Completed recovery repeated a side effect");
    await reopened.OptimizeAsync([Service()], false);
    Assert(s.Archived.Count == 1 && ReferenceEquals(s.Archived[0], original), "Previous history not archived");
    Assert(reopened.State!.Id != original!.Id && b.Stops.Count == 2, "New optimization did not begin");
});
await Test("Incomplete and interrupted recovery never offers a new optimization", async () =>
{
    foreach (var outcome in new[] { Outcome.Stopped, Outcome.PartialStopped, Outcome.Failed, Outcome.RestoreFailed, Outcome.Pending, Outcome.NeedsReview })
    {
        var s = new MemoryStore { State = new() { BootUtc = boot, Entries = [new() { Target = new(Service(), []), Outcome = outcome }] } };
        var b = new FakeBackend(); var e = Engine(b, s);
        Assert(!e.CanOptimize, "Unresolved state permits another optimization");
        await e.OptimizeAsync([Service()], false);
        Assert(b.Stops.Count == 0 && s.Archived.Count == 0, "Unresolved state lost or actions executed");
    }
});
Console.WriteLine($"{passed} tests passed; no user applications or services changed.");

sealed class MemoryStore : IStateStore
{
    public SessionState? State { get; set; }
    public bool FailSave;
    public List<SessionState> Archived = [];
    public SessionState? Load() => State;
    public void Save(SessionState state) { if (FailSave) throw new IOException("disk full"); State = state; }
    public void Archive() { if (State is not null) Archived.Add(State); State = null; }
}
sealed class FakeBackend : IGameBackend
{
    public bool Active = true, FailDocker, ThrowStop, ThrowStart, PartialClose;
    public List<string> Stops = [], Starts = [];
    public Func<Task>? BeforeStart;
    public TaskCompletionSource? Block;
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<TargetSnapshot?> CaptureAsync(Target target) => Task.FromResult(Active ? new TargetSnapshot(target, []) : null);
    public Task<bool> IsActiveAsync(Target target) => Task.FromResult(Active);
    public async Task<ActionResult> StopAsync(TargetSnapshot target)
    {
        Stops.Add(target.Target.Key); Entered.TrySetResult();
        if (Block is not null) await Block.Task;
        if (ThrowStop) throw new UnauthorizedAccessException("UAC cancelled");
        if (FailDocker && target.Target.Kind == TargetKind.Docker) return new(false, false, "Docker failed");
        Active = false; return new(true, !PartialClose, "Stopped");
    }
    public async Task StartAsync(Target target)
    {
        if (ThrowStart) throw new IOException("Start failed");
        if (BeforeStart is not null) await BeforeStart();
        Starts.Add(target.Key); Active = true;
    }
}

sealed class TimeoutBackend(CommandRunner runner) : IGameBackend
{
    public List<string> Starts = [];
    public bool ChildExited;
    public Task<TargetSnapshot?> CaptureAsync(Target target) => Task.FromResult<TargetSnapshot?>(new(target, []));
    public Task<bool> IsActiveAsync(Target target) => Task.FromResult(false);
    public async Task<ActionResult> StopAsync(TargetSnapshot snapshot)
    {
        if (snapshot.Target.Kind == TargetKind.Service) return new(true, true, "Simulated service stop");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var marker = Path.Combine(Path.GetTempPath(), "gmm-timeout-" + Guid.NewGuid().ToString("N") + ".txt");
        var script = "Start-Sleep -Seconds 2; [IO.File]::WriteAllText('" + marker.Replace("'", "''") + "', 'done')";
        try { await runner.RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromMilliseconds(100)); }
        catch (TimeoutException) { ChildExited = File.Exists(marker); throw; }
        finally { if (File.Exists(marker)) File.Delete(marker); }
        return new(false, false, "Unexpected");
    }
    public Task StartAsync(Target target) { Starts.Add(target.Key); return Task.CompletedTask; }
}
