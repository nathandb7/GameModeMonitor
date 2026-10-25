# Game Mode Monitor for Windows 11

<img src="src/GameModeMonitor.App/Assets/GameModeMonitor.png" width="96" height="96" alt="GameModeMonitor rocket and gauge icon" />

## Current release status

This is an early, testable release of the existing monitor, not the completed advanced gaming optimizer. It includes configurable service profiles, safe development-app closure, CPU/RAM monitoring, recovery records and a per-user source installer. It still requests UAC for each privileged service operation.

The planned persistent privileged service/secure IPC, browser background-process closure, game detection, Balanced/Extreme engine, disk monitoring, measured before/after results and redesigned dashboard are **not implemented**. The broom only trims this monitor's working set; it does not demonstrate reduced system resource demand. No measured FPS or system-RAM improvement is claimed.

Automated tests use simulated backends and installer boundaries. Real service/browser optimization, reboot recovery and uninstall behavior require separate interactive verification. The immutable installer redesign passed its independent simulated checks; its additional native review remains pending. Installation does not run optimization.

To change selections, expand the compact bar and open **Configuración del perfil** below the resource indicators. Available service targets are detected separately on each PC, restricted to the recognized catalog; they are not a universal list of safe-to-disable Windows services.

## Configurable gaming profiles

The default **Recomendado** profile preselects recognized automatic-start development services, Windows Search and Print Spooler. **Seguro** limits preselection to automatic development services; **Experimental** additionally includes SysMain and Connected User Experiences/Telemetry and requires an extra warning. Each target is editable and remembered independently per profile. Applications, Docker and WSL are never preselected. Search and printing are temporarily affected; experimental preloading changes can worsen loading times. No profile guarantees an FPS improvement.

Only automatic-start services are eligible for a temporary stop. Their startup types remain unchanged, preserving normal Windows startup after reboot. At the next boot the monitor verifies service recovery without replaying old commands or elevating. Failed or delayed recovery remains visible; failed Revert offers a restart but never forces one. Apps, unsaved projects, containers and Linux terminal sessions are not reconstructed after reboot.

The broom trims only **this monitor's** unused working-set pages, never games, other apps or system standby lists. It reports signed observed system-available-memory change, not an invented globally freed RAM figure. All actions remain explicit; starting with Windows launches the monitor only.

## Install from the repository

See [installation and uninstall instructions](docs/INSTALLATION.md) for the PowerShell command, requirements and preview options. The source installer builds a pinned commit from `nathandb7/GameModeMonitor`, installs per user and creates a monitor-only Windows login entry. It requires the .NET 10 SDK and does not change persistent execution policy. Review downloaded scripts before running them.

Installed tools are immutable per version. A stable `manage.ps1` selects the verified tool payload for updates, rollback and uninstall; updates never overwrite the manager or earlier tools. Legacy installer formats are rejected without changes, rather than being silently migrated.

A portable, local WPF monitor with Spanish UI, live CPU/RAM, a development-process inventory, and reversible gaming actions. It does not promise FPS improvements or eliminate necessary background processes.

## Quick start

1. Open **Modo juego.lnk**, or `publish/GameModeMonitor.App.exe`, as a normal user. It opens as a draggable compact CPU/RAM bar. Click the right arrow to expand the full panel and the left arrow to collapse it. The monitor refuses administrator mode.
2. Save your work, review the editable preselected profile and click **Optimizar**. Launching the monitor never closes anything automatically. Docker/WSL require selection and a separate confirmation; Experimental also requires its own warning acceptance.
3. Click **Revertir** to recover recorded services and reopen recognized apps. Only service operations request UAC; cancelling UAC is reported as a failure.

Keep all files in `publish` together. Portable use requires the x64 .NET 10 Desktop Runtime and creates no login entry. The optional source installer additionally requires the .NET 10 SDK and creates a per-user monitor-only startup entry. Neither mode installs services, changes service startup types or changes permanent execution policy.

## What the buttons actually do

| Target | Optimize | Revert |
|---|---|---|
| Recognized database services | Graceful stop; refuse active dependents | Start only services recorded as successfully stopped |
| Recognized editors/IDEs | Ask their windows to close; wait, never force | Reopen the current catalog executable without stored arguments; skip if running |
| Docker Desktop | Stop local containers with an infinite grace period; stop Desktop without force; verify exit | Reopen Desktop if it was closed successfully |
| All WSL distributions | Explicitly authorized `wsl --shutdown`, only after Docker is no longer active | Do not reconstruct Linux processes or terminal sessions |
| Node/Python/.NET and other development runtimes | Monitor only; no name-based mass termination | Not applicable |

**Important limitations:** application close dialogs may require your input. If a Docker/WSL/helper command exceeds its expected duration, the monitor shows a warning and keeps Optimize/Restore locked until that command actually exits; it never kills the command or starts a competing action. A stuck command can therefore leave the operation pending for manual intervention. WSL shutdown ends Linux processes and sessions, so save first. Reopening an app does not recover unsaved work, terminals, development servers or exact project/session state. Docker may restart containers according to its own restart policies; this app never issues container-start commands or changes those policies.

The safe catalog covers automatic-start MySQL/MariaDB, PostgreSQL, MongoDB, Redis and SQL Server services in protected Program Files installations, plus exact protected Windows services WSearch, Spooler, DiagTrack and SysMain, VS Code, standard Visual Studio 2022 installations, JetBrains IDEs in Program Files, and Docker Desktop. Unrecognized/custom installations are intentionally omitted. Command-line OpenCode/Codex and standalone runtimes are monitor-only. Profile defaults are individually editable; apps/Docker/WSL require selection and confirmation.

## State and recovery

- State is atomically saved under `%LOCALAPPDATA%\GameModeMonitor\session.json` before each stop attempt and before each service/app restoration. Interrupted restoration also requires manual review on restart. Only one monitor instance and one operation are allowed.
- Repeating Optimize cannot overwrite a session. Finish recovery and use **Archivar sesión** before beginning another; archiving explicitly abandons button-based recovery but preserves the JSON record.
- Interrupted actions block all restoration because a child command might still be running after an unexpected monitor exit. Check Windows Services/Docker/WSL manually; only archive after confirming no command is in flight. Unconfirmed results are never silently replayed.
- Restarting Windows leaves normal service startup settings intact. A journal from a previous boot is verified without replaying commands; verified service recovery archives the session. A missing or delayed service stays visible for manual review. Apps normally opened manually still require manual opening.
- Closing or minimizing the monitor does not restore services. Minimize sends it to the tray and stops resource polling; double-click the tray icon to return. Exit from the tray menu or window close button.
- Corrupt or incompatible state fails closed with an error; the original file is retained. Back it up and inspect it manually before removing it. No state file can provide arbitrary executable paths or shell arguments to the helper.

## Build and safe verification

```powershell
dotnet run --project tests/GameModeMonitor.Tests
dotnet build src/GameModeMonitor.App
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -CreateDesktopShortcut
.\publish\GameModeMonitor.App.exe --smoke --output .\artifacts
```

The smoke mode only samples resources, verifies the default bar and expand/collapse, captures both views, minimizes for 6.5 seconds, resumes, writes measurements and exits. It never selects or executes optimization targets. Tests use fake backends; no real application, service, container or distro is stopped. Real optimize/revert and reboot checks require separate explicit authorization.

## Security boundary

The monitor remains unelevated. A small separate helper accepts exactly `start|stop` and one recognized service name. It independently validates the SCM-configured executable in protected Program Files or an exact allowlisted Windows System32 path, validates shared-host DLLs, rejects reparse points and active dependencies, and permits stops only for automatic-start services. It accepts no JSON file, script, arbitrary executable, shell command, startup-type setting or stored arguments. Keep binaries in a folder you control and review every UAC prompt.

Docker container commands always target the local Windows named pipe explicitly, never an inherited remote host/context. No credentials or remote destinations are discovered. The native Windows service manager is local-only.

The compact bar samples only system CPU/RAM, without enumerating processes or services. Full-panel process inventory is polled every two seconds with a non-overlapping refresh guard. Layout changes preserve selections and recovery state. The dark blue panel and circular gauges follow the supplied visual references; system-process termination and GPU telemetry are intentionally not copied.

The build command uses a process-only execution-policy override if local PowerShell script execution is disabled; it does not change the persistent policy.
