# Install Game Mode Monitor

The source installer builds one pinned commit from the public repository and installs it for your Windows account. Windows login starts **only the monitor**, never optimization, cleanup, service operations, or restoration.

## Quick path

Requirements: Windows 11 x64, Windows PowerShell 5.1 or newer, the **.NET 10 SDK** and **.NET 10 Windows Desktop Runtime** (x64), internet access, and enough disk space for a source build. Git and administrator rights are not required. The installer does not install prerequisites.

**Trust warning:** this downloads and executes repository code. Inspect [install.ps1](../install.ps1) and the source first; run it only if you trust the repository and its current maintainers. `Bypass` below applies only to the child PowerShell process, not your permanent execution policy. The repository must have been published before this command works.

Run in a normal, non-administrator PowerShell window:

```powershell
& { $installer = Join-Path ([IO.Path]::GetTempPath()) ('GameModeMonitor-install-' + [guid]::NewGuid().ToString('N') + '.ps1'); Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/nathandb7/GameModeMonitor/main/install.ps1' -OutFile $installer -ErrorAction Stop; powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer }
```

The final message prints the installed executable. A Start Menu shortcut is created. The monitor starts after the installation finishes and at subsequent logins. Neither action invokes Optimize.

Prefer inspection before execution? Run only the download portion first, open the downloaded file in your editor, then run the final `powershell.exe` portion. No `iex` is used.

## Preview and options

After downloading the script, inspect a plan without downloads, builds, directory creation, registry changes, shortcuts, or launches:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -WhatIf
```

Install without a login entry or immediate launch:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -NoAutostart -NoLaunch
```

`-NoAutostart` also removes a previously owned login entry. It never removes an unrelated value. Without this switch, the installer writes the `GameModeMonitor` value in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` as a quoted absolute executable path followed by `--startup`.

To select a specific revision, pass `-Ref` with its complete 40-character hexadecimal commit ID. Branch names other than `main`, tags, URLs and abbreviated IDs are rejected. By default the public GitHub API resolves `main` once; the archive download then uses that immutable commit. No GitHub account, token, Git CLI or authenticated CLI session is used. The bootstrap script itself comes from `main`; for stronger reproducibility, download and inspect the bootstrap script from your chosen commit as well.

## Updates and rollback

Run the installer again to update. Helper and app are published in that order, in Release mode for `win-x64`, into one staged output. The installer checks native build exits and validates required files before activation.

| Location | Purpose |
| --- | --- |
| `%LOCALAPPDATA%\Programs\GameModeMonitor\versions\<commit>` | Immutable per-commit application files |
| `%LOCALAPPDATA%\Programs\GameModeMonitor\installation.json` | Format2 ownership, preparing/ready state and active-version metadata |
| `%LOCALAPPDATA%\Programs\GameModeMonitor\manage.ps1` | Stable manager, created once and never replaced during updates |
| `%LOCALAPPDATA%\GameModeMonitor` | Settings and recovery records; never removed by these scripts |

An existing verified version is reused without overwriting its executable. Older versions are retained. No running app is forcibly closed: close the old monitor yourself before updating if you want to switch immediately, or use `-NoLaunch` and open the new shortcut when ready.

Pinned versions retain their installer, uninstaller and manager factory as hash-verified files. Repeating the downloaded, uniquely named bootstrap works without companion files in the download directory. Runtime and tool files in an existing version are never replaced. The original stable manager selects only the recorded version's fixed, hash-verified tool names; it cannot receive arbitrary tool paths or command text.

To roll back, run the installed installer with the previous full commit ID:

```powershell
& "$env:LOCALAPPDATA\Programs\GameModeMonitor\manage.ps1" -Action Install -Ref '<previous-40-character-commit>' -NoLaunch
```

Replace the placeholder before running it. This re-points activation to the verified retained version; it does not change settings, reconstruct sessions, or perform optimization. An old version no longer present on disk is downloaded and rebuilt from that public commit.

## Uninstall safely

Close the monitor normally first. Preview, then remove the installation's verified files and owned activation:

```powershell
& "$env:LOCALAPPDATA\Programs\GameModeMonitor\manage.ps1" -Action Uninstall -WhatIf
& "$env:LOCALAPPDATA\Programs\GameModeMonitor\manage.ps1" -Action Uninstall
```

If your execution policy blocks these scripts, use the same **process-only** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <full-script-path>` pattern after inspecting them. Do not change a machine/user execution policy just to uninstall.

Settings and recovery data are preserved by default; there is deliberately no data-wipe option. The uninstaller does not revert optimization: use Revert in the app first if a session is active. It never stops services, closes apps, restarts Windows or launches the elevated helper. Locked builds are retained with a warning; retry after closing the app. Owned empty directories are removed non-recursively; unknown entries are preserved. If unknown entries remain after uninstall, inspect them and move the leftover install directory aside before reinstalling; a directory without an ownership manifest is never taken over.

## Failure handling and security boundaries

- Download/build failures occur before shortcuts or login entries are switched. Existing executables and state are untouched. Re-run after correcting the reported issue.
- Existing ownership manifests and SHA-256 file hashes are checked. Missing, modified, extra or unowned version files fail closed rather than being overwritten or deleted. An unrelated `GameModeMonitor` registry value or shortcut is not taken over.
- Install/uninstall operations share a local named lock. Writable paths reject existing reparse points; archive paths reject traversal. Neither script recursively deletes directories. These checks protect normal operations, not a hostile process already running with your Windows account's privileges.
- Build directories are unique system-temp folders. They are retained and printed, including on failure, to avoid unsafe cleanup through a changed path. Inspect and manually remove only the exact reported folder when it is no longer needed. The downloaded bootstrap script is also retained.
- Before switching activation, an atomic format2 manifest records intended and previously owned activation versions. A denied shortcut/registry write can leave mixed activation, but the unchanged stable manager resolves the new verified payload and retry/uninstall still recognize both endpoints. Previous activation ownership clears only after both writes succeed.
- Updates never replace the active manager or old installed tool scripts, even if those files are locked. New tools live only in a new immutable version. A legacy format1 installation is refused before any change; use its original uninstaller first. There is no automatic metadata migration that could strand older validators.
- First installation prepares the manager and ownership record in a sibling directory and exposes them together with a same-volume atomic rename. If interrupted before version activation, rerun the downloaded installer with the exact `-Ref` printed in the error. A different pin is rejected while initialization is incomplete. No service startup type or permanent execution policy is changed.
- Build staging is under `%LOCALAPPDATA%\Temp\i-<unique-id>` on the installation volume so prepared versions can be moved atomically. Staging is retained and reported, never recursively deleted by the installer.

## Verification scope

`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Installer.ps1` validates parsing, side-effect-free previews, failed download/build behavior, pinned-version reuse, ownership guards and data-preserving uninstall with isolated temp fixtures and mocked system boundaries. It never installs into the real account, changes real autostart, downloads code, or shuts down processes/services. Live GitHub downloads and real login/UAC behavior are separate integration checks, not claimed by these simulations.

Read-only startup lookup regression check: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-StartupLookup.ps1`. This reads the current account's startup entry without creating or changing it; an absent value is normal.
