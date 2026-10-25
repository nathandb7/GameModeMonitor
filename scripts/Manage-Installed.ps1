#Requires -Version 5.1
<#
.SYNOPSIS
Stable loader for a versioned Game Mode Monitor installation.
.DESCRIPTION
Updates never replace this file. Only hash-verified named tool payloads from the
recorded version can be invoked. Does not optimize or elevate.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Install', 'Uninstall')][string]$Action = 'Install',
    [ValidatePattern('^(main|[a-fA-F0-9]{40})$')][string]$Ref = 'main',
    [switch]$NoAutostart,
    [switch]$NoLaunch
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-GmmManagerPath {
    param([string]$Path, [string]$Root)
    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not $full.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Tool path escaped installation root.' }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Tool path contains a reparse point.' }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}

function Resolve-GmmManagedTool {
    param([string]$Root, [ValidateSet('Install','Uninstall')][string]$Tool)
    $expected = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\GameModeMonitor')).TrimEnd('\')
    if (-not [IO.Path]::GetFullPath($Root).TrimEnd('\').Equals($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manager is outside the per-user installation.' }
    $marker = Assert-GmmManagerPath (Join-Path $Root 'installation.json') $Root
    if ((Get-Item -LiteralPath $marker).Length -gt 1000000) { throw 'Installation manifest is too large.' }
    $manifest = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($manifest.owner -ne 'nathandb7/GameModeMonitor:source-installer:v2' -or $manifest.schema -ne 2 -or $manifest.current -cnotmatch '^[a-f0-9]{40}$') { throw 'Unsupported installation format; no tool was executed.' }
    $manager = Assert-GmmManagerPath (Join-Path $Root 'manage.ps1') $Root
    if ((Get-FileHash -LiteralPath $manager -Algorithm SHA256).Hash -ne $manifest.scripts.'manage.ps1') { throw 'Stable manager ownership hash mismatch.' }
    $version = Join-Path $Root ('versions\' + $manifest.current)
    $versionMarker = Assert-GmmManagerPath (Join-Path $version 'version.json') $Root
    if (-not (Test-Path -LiteralPath $versionMarker)) { throw "Installation is incomplete. Re-run the downloaded installer with -Ref $($manifest.current)." }
    if ((Get-Item -LiteralPath $versionMarker).Length -gt 2000000) { throw 'Version manifest is too large.' }
    $record = Get-Content -LiteralPath $versionMarker -Raw | ConvertFrom-Json
    if ($record.owner -ne $manifest.owner -or $record.commit -cne $manifest.current) { throw 'Version identity mismatch.' }
    foreach ($name in @('install.ps1','uninstall.ps1')) {
        $entries = @($record.files | Where-Object { $_.path -ceq $name })
        $path = Assert-GmmManagerPath (Join-Path $version $name) $Root
        if ($entries.Count -ne 1 -or $entries[0].sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entries[0].sha256) { throw "Versioned tool hash mismatch: $name" }
    }
    return (Join-Path $version ($Tool.ToLowerInvariant() + '.ps1'))
}

if ($MyInvocation.InvocationName -ne '.') {
    if ($Action -eq 'Uninstall' -and ($PSBoundParameters.ContainsKey('Ref') -or $PSBoundParameters.ContainsKey('NoAutostart') -or $PSBoundParameters.ContainsKey('NoLaunch'))) {
        throw 'Install-only options cannot be passed with -Action Uninstall.'
    }
    $target = Resolve-GmmManagedTool $PSScriptRoot $Action
    $forward = @{}
    foreach ($key in $PSBoundParameters.Keys) { if ($key -ne 'Action') { $forward[$key] = $PSBoundParameters[$key] } }
    & $target @forward
}
