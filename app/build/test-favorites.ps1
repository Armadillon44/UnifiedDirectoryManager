<#
.SYNOPSIS
  Checks the pinned-favourites rules — scoping, de-duplication, ordering, and the settings round trip.

.DESCRIPTION
  Favourites are stored per DOMAIN, because a distinguished name only means something in the domain it came
  from. Get the scoping wrong and the failure is quiet: favourites either leak between domains and fail when
  clicked, or vanish after a restart because the key did not survive the JSON round trip.

  Run with:  pwsh -NoProfile -File ./app/build/test-favorites.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path (Split-Path -Parent $root) 'debug\UnifiedDirectoryManager.dll'
if (-not (Test-Path $dll)) { throw "Build first — could not find $dll" }
[System.Reflection.Assembly]::LoadFrom($dll) | Out-Null

$F = [UnifiedDirectoryManager.Services.Favorites]
$Kind = [UnifiedDirectoryManager.Models.FavoriteKind]

$pass = 0; $fail = 0
function Check([string]$name, $expected, $actual) {
    if ($expected -eq $actual) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else {
        $script:fail++
        Write-Host "  FAIL  $name" -ForegroundColor Red
        Write-Host "          expected: $expected"
        Write-Host "          actual:   $actual"
    }
}
function Entry($kind, [string]$value) {
    $e = New-Object UnifiedDirectoryManager.Models.FavoriteEntry
    $e.Kind = $kind; $e.Value = $value; return $e
}
function NewSettings { New-Object UnifiedDirectoryManager.Services.AppSettings }
function Ou([string]$dn) { Entry $Kind::Container $dn }
function Search([string]$name) { Entry $Kind::SavedSearch $name }

Write-Host "`n== the domain key has to survive a round trip ==" -ForegroundColor Cyan
# The dictionary is rebuilt by the deserialiser with a DEFAULT comparer, so a case-insensitive comparer set
# in code is silently dropped on load. Normalising the key is the only thing that survives.
Check 'the key is lower-cased'      'contoso.net' $F::KeyFor('CONTOSO.NET')
Check 'and trimmed'                 'contoso.net' $F::KeyFor('  Contoso.Net  ')
Check 'no domain means no key'      $null         $F::KeyFor('')
Check 'nor does whitespace'         $null         $F::KeyFor('   ')
Check 'nor null'                    $null         $F::KeyFor($null)

Write-Host "`n== pinning ==" -ForegroundColor Cyan
$s = NewSettings
Check 'nothing is pinned to begin with' 0 (@($F::For($s, 'contoso.net'))).Count
Check 'pinning reports success'         $true  $F::Add($s, 'contoso.net', (Ou 'OU=Sales,DC=contoso,DC=net'))
Check 'and it is there'                 1      (@($F::For($s, 'contoso.net'))).Count
Check 'and reads as pinned'             $true  $F::Contains($s, 'contoso.net', (Ou 'OU=Sales,DC=contoso,DC=net'))

# Pinning the same thing twice is a no-op, not a second row for one target.
Check 'pinning it again is refused'     $false $F::Add($s, 'contoso.net', (Ou 'OU=Sales,DC=contoso,DC=net'))
Check 'and the list is unchanged'       1      (@($F::For($s, 'contoso.net'))).Count
# AD treats a DN without regard to case, so differing casing is the same OU, not a second one.
Check 'different casing is the same OU' $false $F::Add($s, 'contoso.net', (Ou 'ou=sales,dc=contoso,dc=net'))
Check 'and so is stray whitespace'      $false $F::Add($s, 'contoso.net', (Ou '  OU=Sales,DC=contoso,DC=net  '))
Check 'the list is still one'           1      (@($F::For($s, 'contoso.net'))).Count

# A container and a saved search that happen to share a string are different things.
Check 'a saved search of the same name pins' $true (
    $F::Add($s, 'contoso.net', (Search 'OU=Sales,DC=contoso,DC=net')))
Check 'so there are two entries'             2 (@($F::For($s, 'contoso.net'))).Count

Write-Host "`n== order is the order they were pinned ==" -ForegroundColor Cyan
$s = NewSettings
foreach ($n in 'OU=A,DC=x', 'OU=B,DC=x', 'OU=C,DC=x') { [void]$F::Add($s, 'x.net', (Ou $n)) }
Check 'first stays first' 'OU=A,DC=x' (@($F::For($s, 'x.net')))[0].Value
Check 'last stays last'   'OU=C,DC=x' (@($F::For($s, 'x.net')))[2].Value

Write-Host "`n== one domain's pins never show in another ==" -ForegroundColor Cyan
# The whole reason for scoping: a DN from one domain is meaningless in another and fails when clicked.
$s = NewSettings
[void]$F::Add($s, 'contoso.net', (Ou 'OU=Sales,DC=contoso,DC=net'))
Check 'the other domain sees nothing'  0     (@($F::For($s, 'fabrikam.com'))).Count
Check 'and does not think it is pinned' $false $F::Contains($s, 'fabrikam.com', (Ou 'OU=Sales,DC=contoso,DC=net'))
# The same domain in different casing is the SAME domain, not a second bucket.
Check 'casing does not split a domain'  1     (@($F::For($s, 'CONTOSO.NET'))).Count

Write-Host "`n== nothing can be pinned without a domain ==" -ForegroundColor Cyan
# Not connected yet, so there is nothing a distinguished name could be relative to.
$s = NewSettings
Check 'pinning with no domain is refused' $false $F::Add($s, '', (Ou 'OU=Sales,DC=contoso,DC=net'))
Check 'and with a null domain'            $false $F::Add($s, $null, (Ou 'OU=Sales,DC=contoso,DC=net'))
Check 'an empty value is refused too'     $false $F::Add($s, 'contoso.net', (Ou '   '))
Check 'so nothing was stored'             0      $s.Favorites.Count

Write-Host "`n== unpinning ==" -ForegroundColor Cyan
$s = NewSettings
[void]$F::Add($s, 'contoso.net', (Ou 'OU=Sales,DC=contoso,DC=net'))
[void]$F::Add($s, 'contoso.net', (Ou 'OU=IT,DC=contoso,DC=net'))
Check 'unpinning reports success'    $true  $F::Remove($s, 'contoso.net', (Ou 'OU=Sales,DC=contoso,DC=net'))
Check 'and the other one remains'    1      (@($F::For($s, 'contoso.net'))).Count
Check 'unpinning it again is refused' $false $F::Remove($s, 'contoso.net', (Ou 'OU=Sales,DC=contoso,DC=net'))
Check 'unpinning by different casing works' $true (
    $F::Remove($s, 'contoso.net', (Ou 'ou=it,dc=contoso,dc=net')))
# An empty bucket left behind would come back as an empty Favourites section on the next load.
Check 'the empty domain is cleaned up' 0 $s.Favorites.Count

Write-Host "`n== the settings round trip ==" -ForegroundColor Cyan
# The real test of the key: write the settings out and read them back the way the app does.
$s = NewSettings
[void]$F::Add($s, 'CONTOSO.NET', (Ou 'OU=Sales,DC=contoso,DC=net'))
[void]$F::Add($s, 'CONTOSO.NET', (Search 'Disabled users'))
$dir = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-fav-" + [System.Guid]::NewGuid().ToString('N'))
$store = New-Object UnifiedDirectoryManager.Services.SettingsStore $dir
$store.Save($s)
$back = $store.Load()
Check 'both entries come back'        2 (@($F::For($back, 'contoso.net'))).Count
Check 'and are still found by domain' $true $F::Contains($back, 'contoso.net', (Ou 'OU=Sales,DC=contoso,DC=net'))
Check 'whatever casing is asked for'  $true $F::Contains($back, 'Contoso.Net', (Search 'Disabled users'))
Check 'the kind survives'             $Kind::SavedSearch (@($F::For($back, 'contoso.net')))[1].Kind
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue


Write-Host "`n== the tree rows a favourite produces ==" -ForegroundColor Cyan
# These flags decide which right-click entries appear and whether a row navigates anywhere. The favourite
# constructor takes the directory but never calls it, so the rows can be built without a connection.
$Node = [UnifiedDirectoryManager.ViewModels.TreeNodeViewModel]
$onErr = [Action[string]]{ param($m) }
$root = $Node::new([UnifiedDirectoryManager.Models.FavoriteEntry]$null, 'Favourites', $null, $onErr)
$pin  = $Node::new((Ou 'OU=Sales,DC=contoso,DC=net'), 'Sales', $null, $onErr)

Check 'the Favourites row knows what it is' $true  $root.IsFavoritesRoot
Check 'and is not itself a favourite'       $false $root.IsFavorite
Check 'and cannot be pinned'                $false $root.CanPin
# It holds pins; it is not somewhere to navigate to, and it must not look like a container.
Check 'and is not a container'              $false $root.IsContainerNode

Check 'a pinned row is a favourite'         $true  $pin.IsFavorite
Check 'and cannot be pinned again'          $false $pin.CanPin
# A container favourite keeps the REAL distinguished name, so selecting it takes the same path as
# selecting the OU in the tree. That is what makes a favourite a reference rather than a copy.
Check 'it keeps the real DN'                'OU=Sales,DC=contoso,DC=net' $pin.DistinguishedName
# Unpin has to be reachable, or a pin can never be removed.
Check 'and offers a right-click menu'       $true  $pin.HasContextMenu

Write-Host "`n== a favourite that points at nothing says so ==" -ForegroundColor Cyan
Check 'it starts available'          $false $pin.IsUnavailable
Check 'and its glyph is a pin'       '📌'   $pin.Glyph
# The glyph is computed, so WPF only redraws it if the change is announced. Reading the property back
# would pass either way — the binding is what breaks — so watch for the notification instead.
$raised = New-Object System.Collections.Generic.List[string]
$handler = [System.ComponentModel.PropertyChangedEventHandler]{ param($s, $e) $raised.Add($e.PropertyName) }
$pin.add_PropertyChanged($handler)
$pin.IsUnavailable = $true
$pin.remove_PropertyChanged($handler)
Check 'marking it unavailable changes the glyph' '⚠' $pin.Glyph
Check 'and the change is announced'              $true ($raised -contains 'Glyph')
Check 'and the row is still there'               $true $pin.IsFavorite
$search = $Node::new((Search 'Disabled users'), 'Disabled users', $null, $onErr)
Check 'a pinned search has its own glyph' '🔎' $search.Glyph
# A saved search has no DN, so it must not be mistaken for a container to load.
Check 'and is not a container'            $false $search.IsContainerNode

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
