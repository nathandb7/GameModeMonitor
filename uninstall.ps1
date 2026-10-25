#Requires -Version 5.1
<#
.SYNOPSIS
Safely removes owned monitor activation and verified installed files.
.DESCRIPTION
Preserves settings/recovery data and modified, unknown or running files. Does not
stop processes, services, optimize, elevate, or recursively delete directories.
.EXAMPLE
.\uninstall.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install.ps1')

function Remove-GmmEmptyDirectories {
    param([string]$Directory, [string]$Root)
    $pending = New-Object 'Collections.Generic.Queue[string]'
    $directories = New-Object 'Collections.Generic.List[string]'
    $pending.Enqueue($Directory)
    while ($pending.Count) {
        $path = $pending.Dequeue()
        [void](Assert-GmmPath $path $Root)
        $directories.Add($path)
        foreach ($entry in Get-ChildItem -LiteralPath $path -Force) {
            [void](Assert-GmmPath $entry.FullName $Root)
            if ($entry.PSIsContainer) { $pending.Enqueue($entry.FullName) }
        }
    }
    foreach ($path in $directories | Sort-Object Length -Descending) {
        [void](Assert-GmmPath $path $Root)
        # Explicit false never deletes contents, even if a path changes meanwhile.
        try { [IO.Directory]::Delete($path, $false) } catch { Write-Warning "Retaining nonempty or locked directory: $path" }
    }
}

function Invoke-GmmUninstall {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param()
    $root = Get-GmmRoot
    $manifest = Assert-GmmRootOwner $root
    if (-not $manifest) { Write-Host 'No owned installation found.'; return }
    if (-not $PSCmdlet.ShouldProcess($root, 'Remove owned activation and verified files; preserve settings/recovery')) { return }
    $lock = Enter-GmmInstallerLock $root
    try {
    $manifest = Assert-GmmRootOwner $root
    Assert-GmmActivationOwner $root $manifest
    $versions = Assert-GmmPath (Join-Path $root 'versions') $root
    $ownedVersions = @()
    if (Test-Path -LiteralPath $versions) {
        foreach ($directory in Get-ChildItem -LiteralPath $versions -Force) {
            if (-not $directory.PSIsContainer -or $directory.Name -cnotmatch '^[a-f0-9]{40}$') {
                Write-Warning "Retaining unrecognized entry: $($directory.FullName)"; continue
            }
            Assert-GmmVersion $directory.FullName $directory.Name $root
            $ownedVersions += $directory.FullName
        }
    }
    Set-GmmStartup $null
    Set-GmmShortcut $null
    $retained = $false
    foreach ($directory in $ownedVersions) {
        # Acquire exclusive read handles for every file before deleting any. An app
        # in use is retained as a whole; no forced close or partially removed build.
        $handles = New-Object 'Collections.Generic.List[IDisposable]'
        $files = @(Get-GmmFiles $directory)
        try {
            foreach ($file in $files) {
                $handles.Add([IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None))
            }
        } catch {
            $retained = $true
            Write-Warning "Version may be running or locked; retained: $directory"
        } finally { foreach ($handle in $handles) { $handle.Dispose() } }
        if ($retained) { continue }
        foreach ($file in $files) {
            [void](Assert-GmmPath $file.FullName $root)
            try { Remove-Item -LiteralPath $file.FullName -Force } catch { $retained = $true; Write-Warning "Retained locked file: $($file.FullName)" }
        }
        if (-not $retained) { Remove-GmmEmptyDirectories $directory $root }
    }
    if (-not $retained -and (-not (Test-Path -LiteralPath $versions) -or @(Get-GmmFiles $versions).Count -eq 0)) {
        foreach ($name in @('manage.ps1', 'installation.json')) {
            $path = Assert-GmmPath (Join-Path $root $name) $root
            Remove-Item -LiteralPath $path -Force
        }
        foreach ($path in @($versions, $root)) {
            [void](Assert-GmmPath $path $root -AllowRoot)
            if (-not (Test-Path -LiteralPath $path)) { continue }
            try { [IO.Directory]::Delete($path, $false) } catch { Write-Warning "Retaining nonempty or locked directory: $path" }
        }
    }
    Write-Host 'Owned startup entry and shortcut removed. Settings/recovery data are preserved.'
    Write-Host "Locked or unknown entries may remain: $root"
    } finally { $lock.ReleaseMutex(); $lock.Dispose() }
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-GmmUninstall -WhatIf:$WhatIfPreference }
