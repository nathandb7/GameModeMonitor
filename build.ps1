#requires -Version 5.1
[CmdletBinding()]
param([switch]$CreateDesktopShortcut)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = Join-Path $root 'publish'
Push-Location $root
try {
    dotnet run --project tests/GameModeMonitor.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet publish src/GameModeMonitor.ServiceHelper -c Release -r win-x64 --self-contained false -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Helper publish failed.' }
    dotnet publish src/GameModeMonitor.App -c Release -r win-x64 --self-contained false -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $output 'README.md')
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $root 'Modo juego.lnk'))
    $shortcut.TargetPath = Join-Path $output 'GameModeMonitor.App.exe'
    $shortcut.WorkingDirectory = $output
    $shortcut.Description = 'Monitor local de recursos y modo juego reversible'
    $shortcut.IconLocation = $shortcut.TargetPath + ',0'
    $shortcut.Save()
    if ($CreateDesktopShortcut) {
        $desktop = [Environment]::GetFolderPath('Desktop')
        $destination = Join-Path $desktop 'Modo juego.lnk'
        if (Test-Path -LiteralPath $destination) {
            $existing = $shell.CreateShortcut($destination)
            if ($existing.TargetPath -ieq $shortcut.TargetPath) { $existing.IconLocation = $shortcut.IconLocation; $existing.Save() }
            else { Write-Warning 'Desktop shortcut has another target; not overwritten.' }
        }
        else { Copy-Item -LiteralPath (Join-Path $root 'Modo juego.lnk') -Destination $destination }
    }
    Write-Host ('Portable app ready: ' + (Join-Path $output 'GameModeMonitor.App.exe'))
} finally { Pop-Location }
