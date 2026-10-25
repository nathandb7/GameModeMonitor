#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'install.ps1')
# Read-only integration check: a missing registry value must be a normal result.
$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
try { $expected = if ($key) { $key.GetValue('GameModeMonitor', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null } }
finally { if ($key) { $key.Dispose() } }
$actual = Get-GmmStartup
if ($actual -cne $expected) { throw 'Startup lookup changed the stored command or missing-value semantics.' }
Write-Output 'PASS: startup lookup reads an existing or absent value without mutation.'