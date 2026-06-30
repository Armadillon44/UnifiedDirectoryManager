<#
.SYNOPSIS
  Builds per-machine MSI installers for Unified Directory Manager (win-x64 and win-arm64) with WiX v5.

.DESCRIPTION
  Each MSI carries the self-contained single-file UnifiedDirectoryManager.exe (the .NET runtime is bundled, so
  there are no separate prerequisites). By default it publishes the exe first; pass -SkipPublish to
  reuse whatever is already in dist\<rid>.

  Prerequisites (one-time, free WiX v5 — v6+ require a paid maintenance-fee EULA):
    dotnet tool install --global wix --version 5.0.2
    wix extension add -g WixToolset.UI.wixext/5.0.2

.EXAMPLE
  ./build/build-installer.ps1
  ./build/build-installer.ps1 -Runtimes win-arm64
  ./build/build-installer.ps1 -SkipPublish
#>
param(
    [string[]] $Runtimes = @('win-x64', 'win-arm64'),
    [string]   $Configuration = 'Release',
    [string]   $Version,
    [switch]   $SkipPublish
)

$ErrorActionPreference = 'Stop'
$root     = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $root 'src/UnifiedDirectoryManager/UnifiedDirectoryManager.csproj'
$distRoot = Join-Path $root 'dist'
$wxs      = Join-Path $PSScriptRoot 'installer/Product.wxs'
$license  = Join-Path $PSScriptRoot 'installer/notice.rtf'

# Make the dotnet global-tools (wix) reachable even in a fresh shell.
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "WiX not found. Install it with: dotnet tool install --global wix --version 5.0.2"
}

# Version: explicit param, else read <Version> from the csproj.
if (-not $Version) {
    $csproj = Get-Content $project -Raw
    if ($csproj -match '<Version>([^<]+)</Version>') { $Version = $Matches[1].Trim() }
    else { $Version = '1.0.0' }
}
Write-Host "Unified Directory Manager installer build — version $Version" -ForegroundColor Green

# win-<rid> -> WiX -arch token
$archOf = @{ 'win-x64' = 'x64'; 'win-arm64' = 'arm64' }

foreach ($rid in $Runtimes) {
    $arch = $archOf[$rid]
    if (-not $arch) { throw "Unsupported runtime '$rid' (expected win-x64 or win-arm64)." }

    $exe = Join-Path $distRoot "$rid/UnifiedDirectoryManager.exe"
    if (-not $SkipPublish -or -not (Test-Path $exe)) {
        Write-Host "==> Publishing $rid" -ForegroundColor Cyan
        Get-Process UnifiedDirectoryManager -ErrorAction SilentlyContinue | Stop-Process -Force
        dotnet publish $project -c $Configuration -r $rid `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DebugType=none `
            -o (Join-Path $distRoot $rid)
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }
    }
    if (-not (Test-Path $exe)) { throw "Expected published exe not found: $exe" }

    $msi = Join-Path $distRoot "UnifiedDirectoryManager-$arch-$Version.msi"
    Write-Host "==> Building MSI $arch -> $msi" -ForegroundColor Cyan
    wix build $wxs -arch $arch -ext WixToolset.UI.wixext `
        -d "AppExe=$exe" `
        -d "AppVersion=$Version" `
        -d "LicenseRtf=$license" `
        -o $msi
    if ($LASTEXITCODE -ne 0) { throw "WiX build failed for $arch" }
}

Write-Host "`nDone. Installers:" -ForegroundColor Green
foreach ($rid in $Runtimes) {
    $arch = $archOf[$rid]
    $msi = Join-Path $distRoot "UnifiedDirectoryManager-$arch-$Version.msi"
    if (Test-Path $msi) {
        $size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
        Write-Host ("  {0}  ({1} MB)" -f $msi, $size)
    }
}
