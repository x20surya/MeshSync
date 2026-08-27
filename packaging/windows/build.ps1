#!/usr/bin/env pwsh
<#
    Builds the two things a release attaches for Windows: the .msi installer, and the portable
    .exe for a machine somebody would rather not install anything on.

    Needs the .NET 10 SDK and Windows. The WiX toolset is fetched on first use and cached under
    packaging/.tools, the same way build.sh fetches appimagetool, so a clean checkout builds this
    with nothing installed by hand.

    Usage:
      packaging/windows/build.ps1                 # both artifacts, into packaging/windows/out
      packaging/windows/build.ps1 -SkipPublish    # reuse the payload already published there

    WHY THE OUTPUT IS NOT packaging/out. That is build.sh's directory and build.sh removes it
    wholesale on every run. Nothing would notice today, because the two scripts cannot run on the
    same machine - but "the Linux build deletes the Windows installer" is a bad way to find out
    that they can.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$Here = $PSScriptRoot
$Repo = (Resolve-Path (Join-Path $Here '..\..')).Path
$Out = Join-Path $Here 'out'
$Tools = Join-Path $Repo 'packaging\.tools'
$Payload = Join-Path $Out 'payload'
$Portable = Join-Path $Out 'portable'

$WixVersion = '6.0.2'
$Rid = 'win-x64'
$Project = Join-Path $Repo 'src\WinDaemon\WinDaemon.csproj'
$IconFile = Join-Path $Repo 'src\WinDaemon\Resources\meshsync.ico'

# The five WPF and CRT libraries that are not managed assemblies, and so are not bundled by
# anything that only knows about managed assemblies. See the assertions further down: these names
# are the whole reason this script checks its own output rather than trusting the exit code.
$NativeLibraries = @(
    'wpfgfx_cor3.dll'
    'PresentationNative_cor3.dll'
    'D3DCompiler_47_cor3.dll'
    'PenImc_cor3.dll'
    'vcruntime140_cor3.dll'
)

# ───────────────────────────────────────────────────────────────────────────── the version

# Read from Directory.Build.props, which is where the number actually lives - the same source
# build.sh reads, and for the same reason: a file read as text does not expand MSBuild, so
# scraping a .csproj that now says $(MeshSyncVersion) would name the installer after the literal
# string rather than the version.
if (-not $Version) {
    $props = Get-Content (Join-Path $Repo 'Directory.Build.props') -Raw
    if ($props -match '<MeshSyncVersion>([^<]+)</MeshSyncVersion>') { $Version = $Matches[1].Trim() }
}

# Not defaulted to something plausible. An MSI numbered 1.0 installs, upgrades wrongly for ever,
# and looks fine doing it - which is worse than a build that stops here.
if (-not $Version) {
    throw "build.ps1 could not read MeshSyncVersion from Directory.Build.props. Pass -Version."
}

# Windows Installer compares only the first three fields of a version and refuses anything that is
# not numeric, so a tag like v0.7.0-rc1 has to be caught here rather than by msiexec on somebody
# else's machine.
if ($Version -notmatch '^\d+\.\d+(\.\d+)?$') {
    throw "build.ps1 cannot build an MSI for version '$Version': Windows Installer takes x.y.z and nothing else."
}

Write-Host "==> Mesh Sync $Version, $Rid"

# ───────────────────────────────────────────────────────────────────────────── the toolset

# Fetched on first use and cached, rather than assumed to be installed. `dotnet tool install`
# writes into a path we own instead of the user's global tool store, so building this project
# never changes what `wix` means anywhere else on the machine.
$WixExe = Join-Path $Tools 'wix\wix.exe'
if (-not (Test-Path $WixExe)) {
    Write-Host "==> Fetching the WiX toolset $WixVersion"
    New-Item -ItemType Directory -Force (Join-Path $Tools 'wix') | Out-Null
    dotnet tool install wix --version $WixVersion --tool-path (Join-Path $Tools 'wix') | Out-Null
}

# The three extensions the package uses: the installer UI, the firewall rule, and closing the
# running instance before replacing it. They cache under the user's profile, so this is a no-op
# after the first run.
# `wix extension list` exits 2 when the global cache does not exist yet, which is precisely the
# state a clean machine and every CI runner is in - and precisely the state where all three
# extensions do need adding. That is an answer, not a failure, so it is caught rather than thrown.
#
# Joined into one string on purpose, too. Against an array, -notmatch filters it rather than
# answering yes or no, and a non-empty array is true - so the check below would pass every
# extension every time and re-add all three on every build.
$installed = ''
try { $installed = (& $WixExe extension list -g | Out-String) } catch { $installed = '' }
foreach ($ext in @('WixToolset.UI.wixext', 'WixToolset.Firewall.wixext', 'WixToolset.Util.wixext')) {
    if ($installed -notmatch [regex]::Escape($ext)) {
        Write-Host "==> Adding $ext"
        & $WixExe extension add -g "$ext/$WixVersion" | Out-Null
    }
}

# ───────────────────────────────────────────────────────────────────────────── the payload

if (-not $SkipPublish) {
    Remove-Item -Recurse -Force $Out -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $Out | Out-Null

    # Self-contained, so it runs on a machine with no .NET - which is what a person downloading a
    # release has. Not single-file: the installer is copying a folder into Program Files either
    # way, and a bundle would only mean extracting 198 MB into the temp directory on first run.
    Write-Host "==> Publishing the payload"
    dotnet publish $Project -c Release -r $Rid --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -o $Payload | Out-Null

    # Single-file for the portable download, where one file is the entire point.
    Write-Host "==> Publishing the portable executable"
    dotnet publish $Project -c Release -r $Rid --self-contained true `
        -p:PublishSingleFile=true -p:DebugType=none -p:EnableCompressionInSingleFile=true `
        -o $Portable | Out-Null
}

if (-not (Test-Path $Payload)) {
    throw "-SkipPublish was passed, but $Payload does not exist. Run build.ps1 once without it."
}

# Whatever a previous run left. A full build clears the whole directory, but -SkipPublish does not
# and must not - so without this, a version bump leaves the old .msi and .exe sitting beside the
# new ones and the listing at the end names four files when two were built.
Get-ChildItem $Out -File -Filter 'MeshSync-*' | Remove-Item -Force

# ──────────────────────────────────────────────── what the exit code does not tell you
#
# Both publishes above succeed whether or not the native libraries came with them, which is how
# every release up to v0.6.0 shipped an .exe that could not open a window. `dotnet publish`
# returning 0 means it wrote what it was asked for; it does not mean what it wrote can run.

foreach ($lib in $NativeLibraries) {
    if (-not (Test-Path (Join-Path $Payload $lib))) {
        throw "The payload is missing $lib. WPF cannot open a window without it, and the installer would ship an app that starts and then shows nothing."
    }
}
if (-not (Test-Path (Join-Path $Payload 'WinDaemon.exe'))) { throw "The payload has no WinDaemon.exe in it." }

# The portable build's whole claim is that it is one file. If IncludeNativeLibrariesForSelfExtract
# is ever lost from the .csproj, the native libraries reappear here as loose files beside the
# .exe - and the release, which attaches the .exe alone, would silently go back to shipping a
# program that starts headless and stays that way.
$loose = @(Get-ChildItem $Portable -File)
if ($loose.Count -ne 1 -or $loose[0].Name -ne 'WinDaemon.exe') {
    $names = ($loose.Name | Sort-Object) -join ', '
    throw "The portable publish produced $($loose.Count) files instead of one: $names. Anything beside WinDaemon.exe is a file the release does not attach and the download therefore does not have."
}

# ───────────────────────────────────────────────────────────────────────────── the licence

# The installer's licence page wants RTF, and the project's licence is a text file. Converting it
# here rather than checking in a second copy means the two cannot drift: there is one LICENSE, and
# what the installer shows is whatever it currently says.
$LicenseRtf = Join-Path $Out 'License.rtf'
$lines = Get-Content (Join-Path $Repo 'LICENSE')
$body = foreach ($line in $lines) {
    # Backslash first, then the braces - the other order would escape the backslashes the brace
    # replacements had just added. In a .NET replacement string a backslash is a literal, so these
    # read oddly and are right: one backslash becomes two, "{" becomes "\{".
    $escaped = $line -replace '\\', '\\' -replace '\{', '\{' -replace '\}', '\}'
    # RTF is a 7-bit format. Anything above ASCII goes out as a Unicode escape with the one
    # replacement character \uc1 promises, or the licence renders as mojibake from the first
    # typographic quote onwards.
    $sb = [System.Text.StringBuilder]::new()
    foreach ($ch in $escaped.ToCharArray()) {
        if ([int]$ch -gt 127) { [void]$sb.Append('\u' + [int]$ch + '?') } else { [void]$sb.Append($ch) }
    }
    $sb.ToString() + '\par'
}
$rtf = '{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fmodern\fcharset0 Consolas;}}\viewkind4\uc1\pard\f0\fs16 ' +
       ($body -join "`r`n") + '}'
Set-Content -Path $LicenseRtf -Value $rtf -Encoding ascii -NoNewline

# ───────────────────────────────────────────────────────────────────────────── the installer

$Msi = Join-Path $Out "MeshSync-$Version-windows-x64.msi"
Write-Host "==> Building the installer"
& $WixExe build `
    -arch x64 `
    -d "Version=$Version" `
    -d "PayloadDir=$Payload" `
    -d "IconFile=$IconFile" `
    -d "LicenseRtf=$LicenseRtf" `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Firewall.wixext `
    -ext WixToolset.Util.wixext `
    -o $Msi `
    (Join-Path $Here 'MeshSync.wxs')

if (-not (Test-Path $Msi)) { throw "wix build reported success but produced no $Msi." }

Copy-Item (Join-Path $Portable 'WinDaemon.exe') (Join-Path $Out "MeshSync-$Version-windows-x64.exe") -Force

# Only the .wixpdb goes. The publish directories stay so that -SkipPublish has something to skip -
# deleting them would make that switch a lie, and re-publishing 198 MB to change one line of the
# .wxs is the difference between iterating on the installer and not bothering with it. The
# generated licence stays for the same reason: it is what the installer's first page shows, and it
# is easier to look at as a file than to extract from the Binary table of a 61 MB package.
#
# All of it is gitignored, and the release uploads the two MeshSync-* files by name rather than
# the directory, so nothing here reaches an artifact.
Remove-Item -Force (Join-Path $Out "MeshSync-$Version-windows-x64.wixpdb") -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "==> Built into $Out"
Get-ChildItem $Out -File -Filter 'MeshSync-*' | ForEach-Object {
    "{0,10:N1} MB  {1}" -f ($_.Length / 1MB), $_.Name
}
