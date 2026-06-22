<#
.SYNOPSIS
  Publishes Unified Directory Manager as self-contained, single-file executables for win-x64 and win-arm64.

.EXAMPLE
  ./build/publish.ps1
  ./build/publish.ps1 -Runtimes win-arm64
#>
param(
    [string[]] $Runtimes = @('win-x64', 'win-arm64'),
    [string]   $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/UnifiedDirectoryManager/UnifiedDirectoryManager.csproj'
$distRoot = Join-Path $root 'dist'

foreach ($rid in $Runtimes) {
    $out = Join-Path $distRoot $rid
    Write-Host "==> Publishing $rid -> $out" -ForegroundColor Cyan
    dotnet publish $project `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -o $out
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }
}

Write-Host "`nDone. Executables:" -ForegroundColor Green
foreach ($rid in $Runtimes) {
    $exe = Join-Path $distRoot "$rid/UnifiedDirectoryManager.exe"
    if (Test-Path $exe) { Write-Host "  $exe" }
}
