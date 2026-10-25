$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$script:repository = $root
foreach ($name in @('install.ps1', 'uninstall.ps1')) {
    $file = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $file)) { throw "Missing installer artifact: $name" }
    $tokens = $null; $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw ($errors | Out-String) }
}
Write-Host 'PASS: installer parser validity (Windows PowerShell 5.1).'
. (Join-Path $root 'install.ps1')
. (Join-Path $root 'uninstall.ps1')
$script:realExpand = (Get-Command Expand-GmmArchive).ScriptBlock

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('gmt-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$previousLocal = $env:LOCALAPPDATA
$env:LOCALAPPDATA = Join-Path $scratch 'Local'
$script:startup = $null
$script:shortcut = $null
$script:launches = 0
$script:downloads = 0
$script:builds = @()
$script:stages = @()
$script:failure = ''
$script:commit = 'a' * 40
$script:scriptRevision = ''

# All system-boundary operations are replaced before any installer invocation.
# Never invoke real registry, shortcut, process launch, network or dotnet publish.
function Assert-GmmSdk { if ($script:failure -eq 'sdk') { throw 'Simulated missing .NET 10 SDK' } }
function Get-GmmStartup { return $script:startup }
function Set-GmmStartup { param($Command) if ($script:failure -eq 'startup') { throw 'Simulated startup write denied' }; $script:startup = $Command }
function Get-GmmShortcut { return $script:shortcut }
function Get-GmmShortcutPath { return Join-Path $scratch 'Programs\Game Mode Monitor.lnk' }
function Set-GmmShortcut { param($Executable) if ($script:failure -eq 'shortcut') { throw 'Simulated shortcut write denied' }; if ($Executable) { $script:shortcut = [pscustomobject]@{ target = $Executable; arguments = '' } } else { $script:shortcut = $null } }
function Start-GmmMonitor { param($Executable) $script:launches++ }
function Invoke-GmmPublicDownload {
    param($Url, $Destination)
    $script:downloads++
    $stage = Split-Path $Destination -Parent
    if ($script:stages -notcontains $stage) { $script:stages += $stage }
    if ($script:failure -eq 'download') { throw 'Simulated download failure' }
    if ($Url -like '*commits/main') { Write-GmmJson $Destination @{ sha = $script:commit } }
    else { Set-Content -LiteralPath $Destination -Value 'mock archive' }
}
function Expand-GmmArchive {
    param($Archive, $Destination)
    $source = Join-Path $Destination ('GameModeMonitor-' + $script:commit)
    foreach ($project in @('GameModeMonitor.ServiceHelper', 'GameModeMonitor.App')) {
        $directory = Join-Path $source ('src\' + $project)
        [void][IO.Directory]::CreateDirectory($directory)
        Set-Content -LiteralPath (Join-Path $directory ($project + '.csproj')) -Value 'simulated project'
    }
    foreach ($name in @('install.ps1', 'uninstall.ps1')) {
        Copy-Item -LiteralPath (Join-Path $script:repository $name) -Destination $source
        if ($script:scriptRevision) { Add-Content -LiteralPath (Join-Path $source $name) -Value $script:scriptRevision }
    }
    [void][IO.Directory]::CreateDirectory((Join-Path $source 'scripts'))
    Copy-Item -LiteralPath (Join-Path $script:repository 'scripts\Manage-Installed.ps1') -Destination (Join-Path $source 'scripts\Manage-Installed.ps1')
}
function Invoke-GmmDotnet {
    param([string[]]$Arguments)
    $script:builds += ,$Arguments
    if ($script:failure -eq 'build') { throw 'Simulated publish failure' }
    if ($Arguments.Count -ne 10 -or $Arguments[0] -ne 'publish' -or $Arguments[7] -ne 'false') { throw 'Unexpected publish arguments' }
    $out = $Arguments[9]
    [void][IO.Directory]::CreateDirectory($out)
    $name = [IO.Path]::GetFileNameWithoutExtension($Arguments[1])
    Set-Content -LiteralPath (Join-Path $out ($name + '.exe')) -Value $script:commit
}
function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "PASS: $Message"
}
function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern)
    try { & $Action } catch { if ($_.Exception.Message -notlike "*$Pattern*") { throw }; Write-Host "PASS: rejects $Pattern"; return }
    throw "FAIL: expected error $Pattern"
}
try {
    $installRoot = Get-GmmRoot
    Invoke-GmmInstall -WhatIf
    Assert-True (-not (Test-Path -LiteralPath $env:LOCALAPPDATA) -and $script:downloads -eq 0 -and $script:builds.Count -eq 0 -and $script:launches -eq 0) 'WhatIf creates no directories, downloads, builds, or launches'
    Assert-Throws { Invoke-GmmInstall -Ref '../unsafe' } '../unsafe'
    Assert-Throws { Assert-GmmPath (Join-Path $scratch 'outside') $installRoot } 'outside the intended root'
    $script:failure = 'sdk'
    Assert-Throws { Invoke-GmmInstall -NoLaunch } 'missing .NET 10 SDK'
    Assert-True ($script:downloads -eq 0 -and -not (Test-Path -LiteralPath $installRoot)) 'missing SDK fails before downloads or installation writes'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $unsafeZip = Join-Path $scratch 'unsafe.zip'
    $zip = [IO.Compression.ZipFile]::Open($unsafeZip, [IO.Compression.ZipArchiveMode]::Create)
    try { [void]$zip.CreateEntry('../outside.txt') } finally { $zip.Dispose() }
    $expandTarget = Join-Path $scratch 'extract'
    [void][IO.Directory]::CreateDirectory($expandTarget)
    Assert-Throws { & $script:realExpand $unsafeZip $expandTarget } 'Unsafe archive entry'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $scratch 'outside.txt'))) 'archive traversal is rejected before writing outside extraction root'
    $script:failure = 'download'
    Assert-Throws { Invoke-GmmInstall -NoLaunch } 'Simulated download failure'
    Assert-True (-not (Test-Path -LiteralPath $installRoot) -and -not $script:startup -and -not $script:shortcut) 'download failure leaves installation and activation absent'
    $script:failure = 'build'
    Assert-Throws { Invoke-GmmInstall -NoLaunch } 'Simulated publish failure'
    Assert-True (-not (Test-Path -LiteralPath $installRoot) -and -not $script:startup -and -not $script:shortcut) 'build failure leaves installation and activation absent'
    $script:failure = ''
    Invoke-GmmInstall -NoLaunch
    $firstManifest = Read-GmmJson (Join-Path $installRoot 'installation.json')
    Assert-True ($firstManifest.schema -eq 2 -and (Test-Path -LiteralPath (Join-Path $installRoot 'manage.ps1'))) 'installation has schema2 and a stable manager'
    $firstExe = Join-Path $installRoot ('versions\' + $script:commit + '\GameModeMonitor.App.exe')
    Assert-True ($script:startup -ceq ('"' + $firstExe + '" --startup') -and $script:launches -eq 0) 'login entry is quoted monitor-only; NoLaunch does not start it'
    Assert-True ($script:builds[-2][1] -like '*ServiceHelper.csproj' -and $script:builds[-1][1] -like '*App.csproj' -and $script:builds[-2][9] -eq $script:builds[-1][9]) 'helper publishes before app into the same staged output'
    $firstHash = (Get-FileHash -LiteralPath $firstExe).Hash
    $startupBefore = $script:startup
    $script:commit = 'b' * 40
    $script:failure = 'build'
    Assert-Throws { Invoke-GmmInstall -NoLaunch } 'Simulated publish failure'
    Assert-True ($script:startup -ceq $startupBefore -and (Get-FileHash -LiteralPath $firstExe).Hash -eq $firstHash) 'failed update preserves old executable and activation'
    $script:failure = ''
    Invoke-GmmInstall -NoAutostart -NoLaunch
    Assert-True (-not $script:startup -and (Test-Path -LiteralPath $firstExe)) 'update can disable owned startup and retains prior version'
    $downloadsBefore = $script:downloads
    $buildsBefore = $script:builds.Count
    Invoke-GmmInstall -Ref $script:commit -NoLaunch
    Assert-True ($script:downloads -eq $downloadsBefore -and $script:builds.Count -eq $buildsBefore) 'verified pinned version is reused without overwrite or rebuild'
    $bootstrapDirectory = Join-Path $scratch 'bootstrap-only'
    [void][IO.Directory]::CreateDirectory($bootstrapDirectory)
    $bootstrap = Join-Path $bootstrapDirectory 'GameModeMonitor-install-unique.ps1'
    Copy-Item -LiteralPath (Join-Path $root 'install.ps1') -Destination $bootstrap
    $mocks = @{}
    foreach ($name in @('Assert-GmmSdk','Get-GmmStartup','Set-GmmStartup','Get-GmmShortcut','Get-GmmShortcutPath','Set-GmmShortcut','Start-GmmMonitor','Invoke-GmmPublicDownload','Expand-GmmArchive','Invoke-GmmDotnet')) {
        $mocks[$name] = (Get-Command $name).ScriptBlock
    }
    . $bootstrap
    foreach ($name in $mocks.Keys) { Set-Item -Path ('Function:\' + $name) -Value $mocks[$name] }
    Invoke-GmmInstall -Ref $script:commit -NoLaunch
    Assert-True ($script:downloads -eq $downloadsBefore -and $script:builds.Count -eq $buildsBefore) 'standalone uniquely named bootstrap reuses verified installation without companion files'
    $startupBefore = $script:startup
    $script:startup = 'unrelated.exe --do-not-touch'
    Assert-Throws { Invoke-GmmInstall -NoLaunch } 'startup value is not owned'
    Assert-Throws { Invoke-GmmUninstall } 'startup value is not owned'
    Assert-True ($script:startup -ceq 'unrelated.exe --do-not-touch') 'foreign startup value is never removed'
    $script:startup = $startupBefore
    Invoke-GmmUninstall -WhatIf
    Assert-True ((Test-Path -LiteralPath $firstExe) -and $script:startup -ceq $startupBefore) 'uninstall WhatIf preserves files and activation'
    $lockedExe = Join-Path $installRoot ('versions\' + $script:commit + '\GameModeMonitor.App.exe')
    $held = [IO.File]::Open($lockedExe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Invoke-GmmUninstall
        Assert-True ((Test-Path -LiteralPath $lockedExe) -and -not $script:startup -and -not $script:shortcut) 'locked version is retained without forced close; owned activation is removed'
    } finally { $held.Dispose() }
    Invoke-GmmInstall -Ref $script:commit -NoLaunch
    $startupBefore = $script:startup
    $settings = Join-Path $env:LOCALAPPDATA 'GameModeMonitor\settings.json'
    [void][IO.Directory]::CreateDirectory((Split-Path $settings -Parent))
    Set-Content -LiteralPath $settings -Value 'preserve settings'
    $extra = Join-Path $installRoot ('versions\' + ('b' * 40) + '\unknown.txt')
    Set-Content -LiteralPath $extra -Value 'not owned'
    Assert-Throws { Invoke-GmmUninstall } 'unexpected files'
    Assert-True ((Test-Path -LiteralPath $extra) -and $script:startup -ceq $startupBefore) 'unknown version files block uninstall before activation changes'
    # Move this test-owned file aside, never recursively remove test scratch.
    Move-Item -LiteralPath $extra -Destination (Join-Path $scratch 'unknown.txt')
    Invoke-GmmUninstall
    Assert-True (-not $script:startup -and -not $script:shortcut -and -not (Test-Path -LiteralPath $firstExe) -and (Get-Content -LiteralPath $settings -Raw).Trim() -eq 'preserve settings') 'uninstall removes only verified product files and preserves per-user data'
    Assert-True (-not (Test-Path -LiteralPath $installRoot)) 'complete uninstall removes only empty owned directories, allowing reinstall'
    foreach ($boundary in @('shortcut', 'startup')) {
        foreach ($recovery in @('retry', 'uninstall')) {
            $env:LOCALAPPDATA = Join-Path $scratch ($boundary.Substring(0,2) + '-' + $recovery)
            $script:startup = $null
            $script:shortcut = $null
            $script:commit = 'a' * 40
            Invoke-GmmInstall -NoLaunch
            $oldStartup = $script:startup
            $oldShortcut = $script:shortcut.target
            $script:commit = 'b' * 40
            $script:failure = $boundary
            Assert-Throws { Invoke-GmmInstall -NoLaunch } ('Simulated ' + $boundary + ' write denied')
            Assert-True ($script:startup -ceq $oldStartup) "$boundary failure preserves prior startup"
            if ($boundary -eq 'shortcut') { Assert-True ($script:shortcut.target -ceq $oldShortcut) 'shortcut failure preserves prior shortcut' }
            $script:failure = ''
            if ($recovery -eq 'retry') {
                Invoke-GmmInstall -Ref $script:commit -NoLaunch
                $desired = Join-Path (Get-GmmRoot) ('versions\' + $script:commit + '\GameModeMonitor.App.exe')
                Assert-True ($script:startup -ceq ('"' + $desired + '" --startup') -and $script:shortcut.target -ceq $desired) "$boundary failure permits successful retry"
            }
            Invoke-GmmUninstall
            Assert-True (-not $script:startup -and -not $script:shortcut -and -not (Test-Path -LiteralPath (Get-GmmRoot))) "$boundary failure permits $recovery then clean uninstall"
        }
    }
        foreach ($recovery in @('retry', 'uninstall')) {
        $env:LOCALAPPDATA = Join-Path $scratch ('v-' + $recovery)
        $script:startup = $null; $script:shortcut = $null; $script:commit = 'a' * 40
        $script:scriptRevision = ''; Invoke-GmmInstall -NoLaunch
        $ownedRoot = Get-GmmRoot
        $managerPath = Join-Path $ownedRoot 'manage.ps1'
        $oldManagerHash = (Get-FileHash -LiteralPath $managerPath).Hash
        $oldTool = Join-Path $ownedRoot ('versions\' + $script:commit + '\install.ps1')
        $oldToolHash = (Get-FileHash -LiteralPath $oldTool).Hash
        $held = [IO.File]::Open($managerPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $script:commit = 'b' * 40; $script:scriptRevision = '# simulated changed pinned scripts'
        $script:failure = 'shortcut'
        try { Assert-Throws { Invoke-GmmInstall -NoLaunch } 'Simulated shortcut write denied' } finally { $held.Dispose() }
        Assert-True ((Get-FileHash -LiteralPath $managerPath).Hash -eq $oldManagerHash -and (Get-FileHash -LiteralPath $oldTool).Hash -eq $oldToolHash) 'locked stable manager and old version tools are never replaced'
        . $managerPath
        $resolved = Resolve-GmmManagedTool $ownedRoot Install
        Assert-True ($resolved -like '*bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\install.ps1') 'original stable manager resolves verified new tool after interrupted activation'
        [void](Assert-GmmRootOwner $ownedRoot)
        Assert-True ($script:startup -like '*aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa*') 'interrupted activation preserves prior startup'
        $script:failure = ''
        if ($recovery -eq 'retry') {
            Invoke-GmmInstall -Ref $script:commit -NoLaunch
            Assert-True ((Get-Content -LiteralPath $resolved -Raw) -match 'simulated changed pinned scripts') 'retry uses immutable changed pinned scripts'
        }
        Invoke-GmmUninstall
        Assert-True (-not $script:startup -and -not $script:shortcut -and -not (Test-Path -LiteralPath $ownedRoot)) "versioned tools permit $recovery then clean uninstall"
    }
    $script:scriptRevision = ''
    $env:LOCALAPPDATA = Join-Path $scratch 'ir'
    $script:startup = $null; $script:shortcut = $null; $script:commit = 'a' * 40
    $partialRoot = Get-GmmRoot
    [void](Initialize-GmmRoot $partialRoot $script:commit (Join-Path $root 'scripts\Manage-Installed.ps1'))
    $before = (Get-FileHash -LiteralPath (Join-Path $partialRoot 'installation.json')).Hash
    Assert-Throws { Invoke-GmmInstall -Ref ('b' * 40) -NoLaunch } 'Initial installation is incomplete'
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $partialRoot 'installation.json')).Hash -eq $before) 'incomplete first install rejects a different pin without changing ownership'
    Invoke-GmmInstall -Ref $script:commit -NoLaunch
    Assert-True ((Read-GmmJson (Join-Path $partialRoot 'installation.json')).phase -eq 'ready') 'first-install preparing state resumes with the original pin'
    . (Join-Path $partialRoot 'manage.ps1')
    $tool = Resolve-GmmManagedTool $partialRoot Uninstall
    Assert-True ($tool.EndsWith('uninstall.ps1')) 'manager validates fixed uninstall payload'
    $bytes = [IO.File]::ReadAllBytes($tool)
    Add-Content -LiteralPath $tool -Value '# untrusted alteration'
    Assert-Throws { Resolve-GmmManagedTool $partialRoot Uninstall } 'Versioned tool hash mismatch'
    [IO.File]::WriteAllBytes($tool, $bytes)
    Invoke-GmmUninstall
    $env:LOCALAPPDATA = Join-Path $scratch 'pre'
    $script:startup = $null; $script:shortcut = $null; $script:commit = 'a' * 40
    Invoke-GmmInstall -NoLaunch
    $ownedRoot = Get-GmmRoot
    $oldTool = Join-Path $ownedRoot ('versions\' + $script:commit + '\install.ps1')
    $managerPath = Join-Path $ownedRoot 'manage.ps1'
    $before = (Get-FileHash -LiteralPath (Join-Path $ownedRoot 'installation.json')).Hash
    $writer = (Get-Command Write-GmmInstallation).ScriptBlock
    function Write-GmmInstallation { param($Root,$Manifest) throw 'Injected premetadata interruption' }
    $script:commit = 'b' * 40
    Assert-Throws { Invoke-GmmInstall -NoLaunch } 'premetadata interruption'
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $ownedRoot 'installation.json')).Hash -eq $before) 'pre-switch interruption preserves old metadata bytes'
    . $managerPath
    Assert-True ((Resolve-GmmManagedTool $ownedRoot Install) -ceq $oldTool) 'original manager selects original tool before metadata switch'
    Set-Item Function:\Write-GmmInstallation $writer
    . $oldTool
    foreach ($name in $mocks.Keys) { Set-Item -Path ('Function:\' + $name) -Value $mocks[$name] }
    Invoke-GmmInstall -Ref $script:commit -NoLaunch
    Assert-True ((Read-GmmJson (Join-Path $ownedRoot 'installation.json')).current -eq $script:commit) 'original installed installer repairs pre-switch interruption'
    $hashes = @(Get-GmmFiles $ownedRoot | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash })
    & $managerPath -Action Uninstall -WhatIf
    Assert-True ((@(Get-GmmFiles $ownedRoot | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join '|') -ceq ($hashes -join '|')) 'actual manager uninstall forwards WhatIf without file mutation'
    Assert-Throws { & $managerPath -Action Uninstall -NoLaunch } 'Install-only options'
    Assert-Throws { & $managerPath -Bogus } 'Bogus'
    Assert-Throws { Resolve-GmmManagedTool (Join-Path $scratch 'outside') Install } 'outside'
    $script:commit = 'c' * 40; $script:failure = 'startup'
    Assert-Throws { Invoke-GmmInstall -NoLaunch } 'Simulated startup write denied'
    $script:failure = ''
    . (Join-Path (Split-Path $oldTool -Parent) 'uninstall.ps1')
    foreach ($name in $mocks.Keys) { Set-Item -Path ('Function:\' + $name) -Value $mocks[$name] }
    Invoke-GmmUninstall
    Assert-True (-not (Test-Path -LiteralPath $ownedRoot) -and -not $script:startup -and -not $script:shortcut) 'original installed uninstaller handles post-switch mixed activation'
    $env:LOCALAPPDATA = Join-Path $scratch 'legacy-refusal'
    $legacyRoot = Get-GmmRoot
    [void][IO.Directory]::CreateDirectory($legacyRoot)
    $legacyMarker = Join-Path $legacyRoot 'installation.json'
    Write-GmmJson $legacyMarker @{schema=1;owner='nathandb7/GameModeMonitor:source-installer:v1';current=('a'*40)}
    $legacyHash = (Get-FileHash -LiteralPath $legacyMarker).Hash
    Assert-Throws { Invoke-GmmInstall -NoLaunch } 'Legacy or unsupported installer format'
    Assert-Throws { Invoke-GmmUninstall } 'Legacy or unsupported installer format'
    Assert-True ((Get-FileHash -LiteralPath $legacyMarker).Hash -eq $legacyHash) 'legacy installed tooling is never modified by format2 bootstrap'
    $unowned = Join-Path $scratch 'unowned'
    [void][IO.Directory]::CreateDirectory($unowned)
    # Root ownership is checked on a separate fresh per-user location.
    $env:LOCALAPPDATA = Join-Path $scratch 'OtherLocal'
    [void][IO.Directory]::CreateDirectory((Get-GmmRoot))
    Assert-Throws { Invoke-GmmInstall -WhatIf } 'no ownership manifest'
    Write-Host 'All simulated installer checks passed. No network, real startup, service, process or SDK changes were made.'
} finally {
    $env:LOCALAPPDATA = $previousLocal
    Write-Host "SCRATCH: $scratch"
    foreach ($stage in $script:stages) { Write-Host "SCRATCH: $stage" }
}
