<#
.SYNOPSIS
    Publishes the sharpapng CLI for one or more runtime identifiers.

.DESCRIPTION
    NativeAOT links with the target platform's own toolchain, so it cannot cross-compile from one
    operating system to another: a Linux binary has to be built on Linux. Across architectures on
    Linux it can, once the target's cross toolchain and lld are installed. This script publishes
    what the current machine can actually produce and explains what it skipped, rather than
    failing halfway through.

    The full set of release binaries is produced by .github/workflows/release.yml, which runs one
    job per operating system.

.PARAMETER Rid
    Runtime identifiers to publish. Defaults to everything this machine can build.

.PARAMETER Version
    Version stamped on the binaries and archive names.

.PARAMETER OutputPath
    Directory for the packaged archives.

.EXAMPLE
    ./build/publish.ps1
    Publishes every target this machine supports.

.EXAMPLE
    ./build/publish.ps1 -Rid linux-arm -Version 1.2.0
    Publishes a single target.
#>
[CmdletBinding()]
param(
    [string[]] $Rid,
    [string] $Version = '0.1.0',
    [string] $OutputPath = 'dist'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/SharpApng.Cli/SharpApng.Cli.csproj'

# A target on another architecture than the host's can still be built on the same operating
# system: on Windows with the MSVC component VcTools names, on Linux with the cross toolchain
# Triplet names, whose objcopy and lld SharpApng.Cli.csproj selects.
$allTargets = @(
    @{ Rid = 'win-x64';     Os = 'Windows'; Arch = 'X64';   VcTools = 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    @{ Rid = 'win-x86';     Os = 'Windows'; Arch = 'X86';   VcTools = 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    @{ Rid = 'win-arm64';   Os = 'Windows'; Arch = 'Arm64'; VcTools = 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' }
    @{ Rid = 'linux-x64';   Os = 'Linux';   Arch = 'X64' }
    @{ Rid = 'linux-arm64'; Os = 'Linux';   Arch = 'Arm64'; Triplet = 'aarch64-linux-gnu' }
    @{ Rid = 'linux-arm';   Os = 'Linux';   Arch = 'Arm';   Triplet = 'arm-linux-gnueabihf' }
    @{ Rid = 'osx-x64';     Os = 'OSX';     Arch = 'X64' }
    @{ Rid = 'osx-arm64';   Os = 'OSX';     Arch = 'Arm64' }
)

# Returns $null when the target can be built here, or the reason it cannot.
function Get-BlockingReason {
    param($Target)

    $hostOs = if ($IsWindows) { 'Windows' } elseif ($IsLinux) { 'Linux' } else { 'OSX' }
    $hostArch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()

    if ($Target.Os -ne $hostOs) {
        return "NativeAOT needs a $($Target.Os) host"
    }

    # Windows needs nothing beyond the MSVC component, checked below with the rest of MSVC.
    if ($Target.Arch -ne $hostArch -and -not $IsWindows) {
        if (-not $Target.Triplet) { return "NativeAOT needs a $($Target.Os)/$($Target.Arch) host" }

        $triplet = $Target.Triplet
        if (-not (Get-Command "$triplet-objcopy" -ErrorAction SilentlyContinue)) {
            return "the $triplet cross toolchain is not installed (gcc-$triplet, binutils-$triplet and the matching libc6-dev cross package)"
        }
        if (-not (Get-Command ld.lld -ErrorAction SilentlyContinue)) { return 'lld is not installed' }
    }

    # Right platform, but the link step still needs a native toolchain installed.
    if ($IsWindows) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (-not (Test-Path $vswhere)) { return 'Visual Studio C++ build tools not found' }

        $vc = & $vswhere -latest -products * -requires $Target.VcTools -property installationPath 2>$null
        if (-not $vc) {
            if ($Target.Arch -eq 'Arm64') { return 'the MSVC ARM64 build tools are not installed' }
            return 'the MSVC C++ toolchain is not installed (Desktop development with C++)'
        }
    }
    elseif (-not (Get-Command clang -ErrorAction SilentlyContinue)) {
        return 'clang is not installed'
    }

    return $null
}

$requested = if ($Rid) {
    $allTargets | Where-Object { $Rid -contains $_.Rid }
} else {
    $allTargets
}

if (-not $requested) {
    throw "No known target matched. Valid identifiers: $(($allTargets.Rid) -join ', ')"
}

# The NativeAOT link can end up calling vswhere by name, and the Visual Studio installer does not
# put it on PATH; seen with Visual Studio 2026, where the link then fails with "'vswhere.exe' is
# not recognized".
if ($IsWindows) {
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if ((Test-Path $installer) -and ($env:PATH -split ';') -notcontains $installer) {
        $env:PATH = "$installer;$env:PATH"
    }
}

$distPath = Join-Path $repoRoot $OutputPath
New-Item -ItemType Directory -Force -Path $distPath | Out-Null

$built = @()
$skipped = @()

foreach ($target in $requested) {
    $rid = $target.Rid

    $blocked = Get-BlockingReason $target
    if ($blocked) {
        $skipped += $rid
        Write-Host "skip   $rid — $blocked" -ForegroundColor DarkGray
        continue
    }

    Write-Host "build  $rid" -ForegroundColor Cyan
    $publishPath = Join-Path $repoRoot "publish/$rid"

    dotnet publish $project -c Release -r $rid -p:Version=$Version -o $publishPath --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publishing $rid failed." }

    # Ship the executable and the licence only: no symbols, no XML documentation.
    $stage = Join-Path $repoRoot "publish/stage/sharpapng-$Version-$rid"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    $exeName = if ($rid -like 'win-*') { 'SharpApng.Cli.exe' } else { 'SharpApng.Cli' }
    $outName = if ($rid -like 'win-*') { 'sharpapng.exe' } else { 'sharpapng' }
    Copy-Item (Join-Path $publishPath $exeName) (Join-Path $stage $outName)
    Copy-Item (Join-Path $repoRoot 'LICENSE'), (Join-Path $repoRoot 'README.md') $stage

    $archive = Join-Path $distPath "sharpapng-$Version-$rid$(if ($rid -like 'win-*') { '.zip' } else { '.tar.gz' })"
    if ($rid -like 'win-*') {
        Compress-Archive -Path $stage -DestinationPath $archive -Force
    } else {
        tar -czf $archive -C (Split-Path $stage) (Split-Path $stage -Leaf)
    }

    $size = '{0:N1} MB' -f ((Get-Item $archive).Length / 1MB)
    Write-Host "ok     $rid -> $(Split-Path $archive -Leaf) ($size)" -ForegroundColor Green
    $built += $rid
}

Write-Host ''
Write-Host "Built:   $(if ($built) { $built -join ', ' } else { 'nothing' })"
if ($skipped) {
    Write-Host "Skipped: $($skipped -join ', ')"
    Write-Host 'Those targets need a matching host. Push a v* tag to build them all in CI.' -ForegroundColor Yellow
}
