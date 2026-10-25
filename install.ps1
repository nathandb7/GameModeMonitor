#Requires -Version 5.1
<#
.SYNOPSIS
Builds a pinned public source commit and installs Game Mode Monitor for this user.
.DESCRIPTION
Requires Windows x64 and .NET 10 SDK/Desktop Runtime. Never runs optimization,
elevates, downloads an SDK, or modifies persistent execution policy.
.PARAMETER Ref
main (resolved once through the public GitHub API), or a complete 40-digit commit.
.PARAMETER NoAutostart
Do not create a Windows login entry; an existing owned entry is removed.
.PARAMETER NoLaunch
Do not launch the monitor after successful installation.
.EXAMPLE
.\install.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidatePattern('^(main|[a-fA-F0-9]{40})$')][string]$Ref = 'main',
    [switch]$NoAutostart,
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:GmmOwner = 'nathandb7/GameModeMonitor:source-installer:v2'

function Assert-GmmPath {
    param([string]$Path, [string]$Root, [switch]$AllowRoot)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not $full.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase) -and
        -not ($AllowRoot -and $full.Equals($base, [StringComparison]::OrdinalIgnoreCase))) {
        throw "Path is outside the intended root: $full"
    }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not allowed: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $full
}

function Get-GmmRoot {
    if (-not $env:LOCALAPPDATA) { throw 'LOCALAPPDATA is unavailable.' }
    return Assert-GmmPath (Join-Path $env:LOCALAPPDATA 'Programs\GameModeMonitor') $env:LOCALAPPDATA
}

function Read-GmmJson {
    param([string]$Path)
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

function Write-GmmJson {
    param([string]$Path, $Value)
    # Caller owns a freshly created directory, or has validated the prior manifest.
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Write-GmmInstallation {
    param([string]$Root, $Manifest)
    $path = Assert-GmmPath (Join-Path $Root 'installation.json') $Root
    $temporary = Assert-GmmPath (Join-Path $Root ('installation.' + [guid]::NewGuid().ToString('N') + '.tmp')) $Root
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($Manifest | ConvertTo-Json -Depth 8))
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    # A failed replacement leaves the previous complete manifest and diagnostic temp.
    if (Test-Path -LiteralPath $path) { [IO.File]::Replace($temporary, $path, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $path) }
}

function Get-GmmActivationCommits {
    param($Manifest)
    if (-not $Manifest) { return }
    $Manifest.current
    if ($Manifest.PSObject.Properties.Name -contains 'previousActivation') {
        foreach ($commit in @($Manifest.previousActivation)) {
            if ($commit -cnotmatch '^[a-f0-9]{40}$') { throw 'Invalid previous activation version in manifest.' }
            $commit
        }
    }
}

function Assert-GmmRootOwner {
    param([string]$Root)
    [void](Assert-GmmPath $Root (Get-GmmRoot) -AllowRoot)
    if (-not (Test-Path -LiteralPath $Root)) { return $null }
    $marker = Assert-GmmPath (Join-Path $Root 'installation.json') $Root
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
        throw 'Existing installation directory has no ownership manifest; nothing was changed.'
    }
    $manifest = Read-GmmJson $marker
    if ($manifest.owner -ne $script:GmmOwner -or $manifest.schema -ne 2) {
        throw 'Legacy or unsupported installer format. Use its original uninstaller first; nothing was changed.'
    }
    if ($manifest.current -cnotmatch '^[a-f0-9]{40}$' -or $manifest.phase -notin @('preparing','ready')) { throw 'Invalid installation state.' }
    [void]@(Get-GmmActivationCommits $manifest)
    $manager = Assert-GmmPath (Join-Path $Root 'manage.ps1') $Root
    if ($manifest.scripts.'manage.ps1' -notmatch '^[a-fA-F0-9]{64}$' -or
        -not (Test-Path -LiteralPath $manager -PathType Leaf) -or
        (Get-FileHash -LiteralPath $manager -Algorithm SHA256).Hash -ne $manifest.scripts.'manage.ps1') {
        throw 'Stable manager was changed or is missing; nothing was overwritten.'
    }
    return $manifest
}

function Get-GmmFiles {
    param([string]$Directory)
    # Deliberately walk one level at a time: Get-ChildItem -Recurse can follow links.
    $pending = New-Object 'Collections.Generic.Queue[string]'
    $pending.Enqueue($Directory)
    while ($pending.Count) {
        $folder = $pending.Dequeue()
        foreach ($entry in Get-ChildItem -LiteralPath $folder -Force) {
            [void](Assert-GmmPath $entry.FullName $Directory)
            if ($entry.PSIsContainer) { $pending.Enqueue($entry.FullName) }
            else { $entry }
        }
    }
}

function Initialize-GmmRoot {
    param([string]$Root, [string]$Commit, [string]$ManagerSource)
    if (Test-Path -LiteralPath $Root) { throw 'Installation root appeared unexpectedly; nothing was overwritten.' }
    $parent = Split-Path $Root -Parent
    [void](Assert-GmmPath $parent $env:LOCALAPPDATA)
    [void][IO.Directory]::CreateDirectory($parent)
    $initial = Assert-GmmPath (Join-Path $parent ('.GameModeMonitor-init-' + [guid]::NewGuid().ToString('N'))) $parent
    [void][IO.Directory]::CreateDirectory($initial)
    Copy-Item -LiteralPath $ManagerSource -Destination (Join-Path $initial 'manage.ps1')
    $hash = (Get-FileHash -LiteralPath (Join-Path $initial 'manage.ps1') -Algorithm SHA256).Hash
    Write-GmmInstallation $initial @{ schema = 2; owner = $script:GmmOwner; phase = 'preparing'; current = $Commit; scripts = @{ 'manage.ps1' = $hash }; previousActivation = @() }
    [void](Assert-GmmPath $Root $env:LOCALAPPDATA)
    [IO.Directory]::Move($initial, $Root)
    return (Assert-GmmRootOwner $Root)
}

function Assert-GmmVersion {
    param([string]$Directory, [string]$Commit, [string]$Root)
    [void](Assert-GmmPath $Directory $Root)
    $manifestPath = Assert-GmmPath (Join-Path $Directory 'version.json') $Directory
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Version has no ownership manifest.' }
    $manifest = Read-GmmJson $manifestPath
    if ($manifest.owner -ne $script:GmmOwner -or $manifest.commit -cne $Commit) { throw 'Version ownership mismatch.' }
    $actual = @(Get-GmmFiles $Directory | Where-Object { $_.FullName -ine $manifestPath })
    $expected = @($manifest.files)
    if ($actual.Count -ne $expected.Count) { throw 'Version contains missing or unexpected files.' }
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $expected) {
        if ($entry.path -match '(^[\\/]|:|(^|[\\/])\.\.([\\/]|$))' -or $entry.path -eq 'version.json') {
            throw 'Invalid path in version manifest.'
        }
        $path = Assert-GmmPath (Join-Path $Directory $entry.path) $Directory
        if (-not $seen.Add($path)) { throw 'Duplicate path in version manifest.' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Version file changed: $($entry.path)"
        }
    }
    foreach ($required in @('GameModeMonitor.App.exe', 'GameModeMonitor.ServiceHelper.exe', 'install.ps1', 'uninstall.ps1', 'Manage-Installed.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $required) -PathType Leaf)) { throw "Missing $required" }
    }
}

function Invoke-GmmPublicDownload {
    param([string]$Url, [string]$Destination)
    # Only fixed public HTTPS endpoints; do not consult gh, git, credentials, or cookies.
    if ($Url -notmatch '^https://(api\.github\.com/repos/nathandb7/GameModeMonitor/commits/main|codeload\.github\.com/nathandb7/GameModeMonitor/zip/[a-f0-9]{40})$') {
        throw 'Download URL is not an allowed public endpoint.'
    }
    Add-Type -AssemblyName System.Net.Http
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.UseDefaultCredentials = $false
    $handler.Credentials = $null
    $handler.UseCookies = $false
    $handler.AllowAutoRedirect = $false
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(5)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('GameModeMonitor-SourceInstaller/1.0')
    try {
        $response = $client.GetAsync($Url).GetAwaiter().GetResult()
        try {
            [void]$response.EnsureSuccessStatusCode()
            $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            [IO.File]::WriteAllBytes($Destination, $bytes)
        } finally { $response.Dispose() }
    } finally { $client.Dispose(); $handler.Dispose() }
}

function Expand-GmmArchive {
    param([string]$Archive, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.Contains(':') -or $entry.FullName.Contains('\') -or
                $entry.FullName -match '(^/|(^|/)\.\.(/|$))' -or
                (($entry.ExternalAttributes -shr 16) -band 61440) -eq 40960) { throw 'Unsafe archive entry.' }
            $path = Assert-GmmPath (Join-Path $Destination $entry.FullName) $Destination
            if (-not $entry.Name) { [void][IO.Directory]::CreateDirectory($path); continue }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
        }
    } finally { $zip.Dispose() }
}

function Invoke-GmmDotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE; activation was not changed." }
}

function Assert-GmmSdk {
    if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
        throw 'Run x64 Windows PowerShell on x64 Windows; this installer targets win-x64.'
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 10 SDK and Windows Desktop Runtime first; no SDK is downloaded automatically.' }
    $sdks = & dotnet --list-sdks
    if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^10\.')) { throw '.NET 10 SDK is required; activation was not changed.' }
    $runtimes = & dotnet --list-runtimes
    if ($LASTEXITCODE -ne 0 -or -not ($runtimes -match '^Microsoft.WindowsDesktop.App 10\.')) { throw '.NET 10 Windows Desktop Runtime is required.' }
}

function Get-GmmStartup {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path $key) {
        $registryKey = Get-Item -LiteralPath $key
        try { return $registryKey.GetValue('GameModeMonitor', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
        finally { $registryKey.Dispose() }
    }
    return $null
}

function Set-GmmStartup {
    param([AllowNull()][string]$Command)
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if ($Command) {
        [void](New-Item -Path $key -Force)
        [void](New-ItemProperty -LiteralPath $key -Name GameModeMonitor -Value $Command -PropertyType String -Force)
    } elseif ($null -ne (Get-GmmStartup)) { Remove-ItemProperty -LiteralPath $key -Name GameModeMonitor }
}

function Get-GmmShortcutPath {
    $programs = [Environment]::GetFolderPath('Programs')
    return Assert-GmmPath (Join-Path $programs 'Game Mode Monitor.lnk') $programs
}

function Get-GmmShortcut {
    $path = Get-GmmShortcutPath
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($path)
    return [pscustomobject]@{ target = $shortcut.TargetPath; arguments = $shortcut.Arguments }
}

function Set-GmmShortcut {
    param([AllowNull()][string]$Executable)
    $path = Get-GmmShortcutPath
    if (-not $Executable) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }; return }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = $Executable
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $shortcut.Description = 'Game Mode Monitor'
    $shortcut.IconLocation = $Executable + ',0'
    $shortcut.Save()
}

function Assert-GmmActivationOwner {
    param([string]$Root, $Manifest)
    $startup = Get-GmmStartup
    $shortcut = Get-GmmShortcut
    $startupOwned = -not $startup
    $shortcutOwned = -not $shortcut
    foreach ($commit in @(Get-GmmActivationCommits $Manifest | Select-Object -Unique)) {
        $directory = Join-Path $Root ('versions\' + $commit)
        $exe = Join-Path $directory 'GameModeMonitor.App.exe'
        $matchesStartup = $startup -and $startup -ceq ('"' + $exe + '" --startup')
        $matchesShortcut = $shortcut -and $shortcut.target -ieq $exe -and -not $shortcut.arguments
        if ($matchesStartup -or $matchesShortcut) {
            # Recorded ownership alone never authorizes an arbitrary or altered build.
            Assert-GmmVersion $directory $commit $Root
            if ($matchesStartup) { $startupOwned = $true }
            if ($matchesShortcut) { $shortcutOwned = $true }
        }
    }
    if (-not $startupOwned) { throw 'Existing GameModeMonitor startup value is not owned; nothing was changed.' }
    if (-not $shortcutOwned) { throw 'Existing shortcut is not owned; nothing was changed.' }
}

function Start-GmmMonitor {
    param([string]$Executable)
    Start-Process -FilePath $Executable
}

function Enter-GmmInstallerLock {
    param([string]$Root)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Root.ToLowerInvariant()))).Replace('-', '') }
    finally { $hash.Dispose() }
    $mutex = New-Object Threading.Mutex($false, ('Local\GameModeMonitor-Installer-' + $key))
    try {
        $acquired = $false
        try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'Another installer/uninstaller is active; retry after it finishes.' }
        return $mutex
    } catch { $mutex.Dispose(); throw }
}

function Invoke-GmmInstall {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([ValidatePattern('^(main|[a-fA-F0-9]{40})$')][string]$Ref = 'main', [switch]$NoAutostart, [switch]$NoLaunch)
    $root = Get-GmmRoot
    $prior = Assert-GmmRootOwner $root
    if ($prior -and $prior.phase -eq 'ready') { Assert-GmmVersion (Join-Path $root ('versions\' + $prior.current)) $prior.current $root }
    Assert-GmmActivationOwner $root $prior
    if (-not $PSCmdlet.ShouldProcess($root, 'Download pinned public source, build and install monitor (never optimize)')) { return }
    Assert-GmmSdk
    $lock = Enter-GmmInstallerLock $root
    $stage = $null
    try {
    # Revalidate after acquiring the shared install/uninstall lock.
    $prior = Assert-GmmRootOwner $root
    Assert-GmmActivationOwner $root $prior
    if ($prior -and $prior.phase -eq 'ready') { Assert-GmmVersion (Join-Path $root ('versions\' + $prior.current)) $prior.current $root }
    $tempRoot = Assert-GmmPath (Join-Path $env:LOCALAPPDATA 'Temp') $env:LOCALAPPDATA
    [void][IO.Directory]::CreateDirectory($tempRoot)
    $stage = Assert-GmmPath (Join-Path $tempRoot ('i-' + [guid]::NewGuid().ToString('N'))) $tempRoot
    [void][IO.Directory]::CreateDirectory($stage)
        $commit = $Ref.ToLowerInvariant()
        if ($Ref -eq 'main') {
            $resolution = Join-Path $stage 'commit.json'
            Invoke-GmmPublicDownload 'https://api.github.com/repos/nathandb7/GameModeMonitor/commits/main' $resolution
            $commit = (Read-GmmJson $resolution).sha
            if ($commit -cnotmatch '^[a-f0-9]{40}$') { throw 'GitHub did not return a complete source commit.' }
        }
        if ($prior -and $prior.phase -eq 'preparing' -and $prior.current -cne $commit) { throw "Initial installation is incomplete. Re-run with -Ref $($prior.current)." }
        $version = Assert-GmmPath (Join-Path $root ('versions\' + $commit)) $root
        if (Test-Path -LiteralPath $version) {
            Assert-GmmVersion $version $commit $root
            Write-Host "Reusing verified version $commit; no running executable is overwritten."
        } else {
            $archive = Join-Path $stage 'source.zip'
            Invoke-GmmPublicDownload ("https://codeload.github.com/nathandb7/GameModeMonitor/zip/$commit") $archive
            $sourceRoot = Join-Path $stage 'source'
            [void][IO.Directory]::CreateDirectory($sourceRoot)
            Expand-GmmArchive $archive $sourceRoot
            $source = Assert-GmmPath (Join-Path $sourceRoot ('GameModeMonitor-' + $commit)) $sourceRoot
            $output = Join-Path $stage 'output'
            foreach ($project in @('GameModeMonitor.ServiceHelper', 'GameModeMonitor.App')) {
                $projectPath = Join-Path $source ("src\$project\$project.csproj")
                if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw "Missing source project: $project" }
                Invoke-GmmDotnet -Arguments @('publish', $projectPath, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', $output)
            }
            foreach ($name in @('install.ps1', 'uninstall.ps1')) {
                $sourceScript = Assert-GmmPath (Join-Path $source $name) $source
                if (-not (Test-Path -LiteralPath $sourceScript -PathType Leaf)) { throw "Pinned source is missing $name" }
                Copy-Item -LiteralPath $sourceScript -Destination (Join-Path $stage $name)
                Copy-Item -LiteralPath $sourceScript -Destination (Join-Path $output $name)
            }
            $managerSource = Assert-GmmPath (Join-Path $source 'scripts\Manage-Installed.ps1') $source
            Copy-Item -LiteralPath $managerSource -Destination (Join-Path $output 'Manage-Installed.ps1')
            $files = @(Get-GmmFiles $output | ForEach-Object {
                @{ path = $_.FullName.Substring($output.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
            })
            Write-GmmJson (Join-Path $output 'version.json') @{ owner = $script:GmmOwner; commit = $commit; files = $files }
            Assert-GmmVersion $output $commit $stage
            # Recheck before creating persistent state. No old version is overwritten/deleted.
            [void](Assert-GmmRootOwner $root)
            if (-not $prior) { $prior = Initialize-GmmRoot $root $commit (Join-Path $output 'Manage-Installed.ps1') }
            [void][IO.Directory]::CreateDirectory((Assert-GmmPath (Join-Path $root 'versions') $root))
            [IO.Directory]::Move($output, $version)
        }
        # Payload scripts are immutable inside their verified version. Root tools
        # are never copied or replaced; the original generic manager stays intact.
        Assert-GmmActivationOwner $root $prior
        $previousActivation = @(Get-GmmActivationCommits $prior | Where-Object { $_ -cne $commit } | Select-Object -Unique)
        $activation = @{ schema = 2; owner = $script:GmmOwner; phase = 'ready'; current = $commit; scripts = $prior.scripts; previousActivation = $previousActivation }
        Write-GmmInstallation $root $activation
        $exe = Join-Path $version 'GameModeMonitor.App.exe'
        Assert-GmmActivationOwner $root ([pscustomobject]$activation)
        Set-GmmShortcut $exe
        Assert-GmmActivationOwner $root ([pscustomobject]$activation)
        if ($NoAutostart) { Set-GmmStartup $null } else { Set-GmmStartup ('"' + $exe + '" --startup') }
        $activation.previousActivation = @()
        Write-GmmInstallation $root $activation
        Write-Host "Installed monitor: $exe"
        Write-Host 'Settings and recovery data are unchanged. Previous versions are retained.'
        if (-not $NoLaunch) { Start-GmmMonitor $exe }
    } finally {
        # Retention is intentional: no recursive deletion can traverse a raced link.
        if ($stage) { Write-Host "Build staging retained for inspection; remove manually when no longer needed: $stage" }
        $lock.ReleaseMutex()
        $lock.Dispose()
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-GmmInstall -Ref $Ref -NoAutostart:$NoAutostart -NoLaunch:$NoLaunch -WhatIf:$WhatIfPreference
}
