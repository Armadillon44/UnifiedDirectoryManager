<#
.SYNOPSIS
  Regression tests for F8: a torn settings write silently destroys every pinned favourite.

.DESCRIPTION
  Two separate failures made one loss.

  Writing: File.WriteAllText opens the destination with truncate. Between the truncate and the write the
  real file is zero bytes, and a crash, a power cut or a full disk in that window leaves it that way. The
  fix writes a sibling temp file and renames it over the top, so a reader sees either the whole old file or
  the whole new one.

  Reading: an unreadable file used to be logged and then treated as absent, returning defaults. That reads
  exactly like a first run, and the very next save overwrote the damaged file — which usually still held
  most of the favourites — with the empty defaults. The fix renames it aside, numbered, and reports the name
  through ISettingsStore.RecoveredFrom so the operator can be told before anything else is written.

  These run against a real directory under the user temp folder, so they exercise the actual file
  operations rather than asserting that some code was written. Everything is cleaned up at the end.

  Run with:  pwsh -NoProfile -File ./app/build/test-settings-store.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dll = Join-Path $repoRoot 'debug\UnifiedDirectoryManager.dll'
if (-not (Test-Path $dll)) { throw "Build first — could not find $dll" }
[System.Reflection.Assembly]::LoadFrom($dll) | Out-Null

$pass = 0; $fail = 0
function Check([string]$name, $expected, $actual) {
    if ($expected -eq $actual) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else {
        $script:fail++
        Write-Host "  FAIL  $name" -ForegroundColor Red
        Write-Host "          expected: [$expected]"
        Write-Host "          actual:   [$actual]"
    }
}

# A fresh directory per run, so a previous run's leftovers cannot make a test pass.
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-settings-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
$settingsPath = Join-Path $sandbox 'settings.json'

function New-Store { [UnifiedDirectoryManager.Services.SettingsStore]::new($sandbox) }

# Builds settings carrying one pinned favourite, which is the data this finding is about.
function New-SettingsWithFavorite([string]$dn) {
    $s = [UnifiedDirectoryManager.Services.AppSettings]::new()
    $s.LastDomainFqdn = 'contoso.net'
    $fav = [UnifiedDirectoryManager.Models.FavoriteEntry]::new()
    $fav.Kind = [UnifiedDirectoryManager.Models.FavoriteKind]::Container
    $fav.Value = $dn
    $list = [System.Collections.Generic.List[UnifiedDirectoryManager.Models.FavoriteEntry]]::new()
    $list.Add($fav)
    $s.Favorites['contoso.net'] = $list
    return $s
}

try {

Write-Host "`n== a save round-trips, and leaves nothing half-written behind ==" -ForegroundColor Cyan

$store = New-Store
Check 'a clean first load reports no recovery' $null $store.RecoveredFrom

$store.Save((New-SettingsWithFavorite 'OU=Sales,DC=contoso,DC=net'))
Check 'the settings file exists after a save' $true (Test-Path $settingsPath)
# The temp file is an implementation detail of the atomic write, but a leftover one is not: it means the
# rename did not happen, and the next save would find a stale sibling.
Check 'no .tmp is left next to it'            $false (Test-Path ($settingsPath + '.tmp'))

$loaded = (New-Store).Load()
Check 'the domain survives the round trip'    'contoso.net' $loaded.LastDomainFqdn
Check 'so does the pinned favourite'          'OU=Sales,DC=contoso,DC=net' $loaded.Favorites['contoso.net'][0].Value

Write-Host "`n== overwriting an existing file replaces it, and still leaves no .tmp ==" -ForegroundColor Cyan
# The first save takes File.Move (no destination); every later save takes File.Replace. Both paths need
# covering — an atomic write that only works on the first save is not an atomic write.
$store.Save((New-SettingsWithFavorite 'OU=Marketing,DC=contoso,DC=net'))
Check 'no .tmp after the replace path'        $false (Test-Path ($settingsPath + '.tmp'))
$reloaded = (New-Store).Load()
Check 'the second save won'                   'OU=Marketing,DC=contoso,DC=net' $reloaded.Favorites['contoso.net'][0].Value
Check 'and only one settings file exists'     1 (@(Get-ChildItem $sandbox -Filter 'settings.json*').Count)

Write-Host "`n== an unreadable file is kept, not discarded (the actual data loss) ==" -ForegroundColor Cyan
# What a torn write left behind: a truncated file. It still holds most of the favourites, and it used to be
# thrown away by the next save.
$truncated = '{"lastDomainFqdn":"contoso.net","favorites":{"contoso.net":[{"kind":0,"value":"OU=Sal'
Set-Content -LiteralPath $settingsPath -Value $truncated -NoNewline -Encoding UTF8

$recovering = New-Store
$defaults = $recovering.Load()
Check 'a torn file loads as defaults'         $null $defaults.LastDomainFqdn
Check 'and the recovery is reported'          'settings.bad-1.json' $recovering.RecoveredFrom
Check 'the unreadable file is moved aside'    $false (Test-Path $settingsPath)
Check 'the kept copy exists'                  $true (Test-Path (Join-Path $sandbox 'settings.bad-1.json'))
# Kept byte-for-byte: the point of keeping it is that the favourites can be read out of it by hand.
Check 'and holds the original bytes'          $truncated (Get-Content -LiteralPath (Join-Path $sandbox 'settings.bad-1.json') -Raw)

Write-Host "`n== a second bad start does not destroy the evidence from the first ==" -ForegroundColor Cyan
Set-Content -LiteralPath $settingsPath -Value 'not json at all' -NoNewline -Encoding UTF8
$second = New-Store
$second.Load() | Out-Null
Check 'the second is numbered separately'     'settings.bad-2.json' $second.RecoveredFrom
Check 'the first copy is untouched'           $truncated (Get-Content -LiteralPath (Join-Path $sandbox 'settings.bad-1.json') -Raw)

Write-Host "`n== RecoveredFrom is per-load, not sticky ==" -ForegroundColor Cyan
# It drives a startup warning. Left set, it would warn on every later load in the same session and the
# operator would learn to dismiss it.
$second.Save([UnifiedDirectoryManager.Services.AppSettings]::new())
$second.Load() | Out-Null
Check 'a good load clears it'                 $null $second.RecoveredFrom
Check 'a fresh store on a good file is clear' $null ((New-Store) | ForEach-Object { $_.Load() | Out-Null; $_.RecoveredFrom })

Write-Host "`n== an empty file is a torn write too ==" -ForegroundColor Cyan
# Zero bytes is exactly what the truncate window produces. JsonSerializer throws on it, so it takes the
# recovery path — worth pinning, because "" deserialising to null instead would slip through as a first run.
Set-Content -LiteralPath $settingsPath -Value '' -NoNewline -Encoding UTF8
$third = New-Store
$third.Load() | Out-Null
Check 'an empty file is set aside as well'    'settings.bad-3.json' $third.RecoveredFrom

Write-Host "`n== the write path itself (mutation check) ==" -ForegroundColor Cyan
# The truncate window cannot be observed from here — reproducing it means killing the process mid-write. So
# assert the shape instead: the destination must never be handed to a truncating writer. Reverting the fix
# to File.WriteAllText(_path, ...) flips this.
$src = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Services\SettingsStore.cs')
$saveSrc = [regex]::Match($src, '(?s)public void Save\(AppSettings settings\).*?\r?\n    \}').Value
Check 'Save never writes straight to the destination' $false ($saveSrc -match 'WriteAllText\(\s*_path\b')
Check 'Save writes a temp file'                       $true  ($saveSrc -match 'WriteAllText\(\s*temp\b')
Check 'and renames it into place'                     $true  ($saveSrc -match 'File\.(Replace|Move)\(')

# And the startup path has to say so out loud, or the recovery is as silent as the loss was.
$appSrc = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\App.xaml.cs')
Check 'startup reads RecoveredFrom'                   $true ($appSrc -match 'settingsStore\.RecoveredFrom')
Check 'and shows the operator a message'              $true ($appSrc -match '(?s)RecoveredFrom is \{ \} bad.{0,600}MessageBox\.Show')

}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`npass=$pass fail=$fail"
if ($fail -gt 0) { exit 1 }
