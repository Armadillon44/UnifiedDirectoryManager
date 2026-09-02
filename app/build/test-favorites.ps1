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

Write-Host "`n== reordering ==" -ForegroundColor Cyan
# The order is the operator's, so it is stored rather than derived. A move that cannot happen has to report
# false: the caller saves and rebuilds on true, and a silent "true" for a move off the end would rewrite the
# settings file and redraw the tree for nothing.
$s = NewSettings
foreach ($n in 'OU=A,DC=x', 'OU=B,DC=x', 'OU=C,DC=x') { [void]$F::Add($s, 'x.net', (Ou $n)) }
function Order { (@($F::For($s, 'x.net')) | ForEach-Object { $_.Value }) -join ',' }

Check 'moving the last one up reports success' $true $F::Move($s, 'x.net', (Ou 'OU=C,DC=x'), -1)
Check 'and it swaps with the one above'  'OU=A,DC=x,OU=C,DC=x,OU=B,DC=x' (Order)
Check 'moving it up again'               $true $F::Move($s, 'x.net', (Ou 'OU=C,DC=x'), -1)
Check 'takes it to the top'              'OU=C,DC=x,OU=A,DC=x,OU=B,DC=x' (Order)
# Already at the top: nothing to swap with, so nothing happens and the caller is told so.
Check 'moving past the top is refused'   $false $F::Move($s, 'x.net', (Ou 'OU=C,DC=x'), -1)
Check 'and the order is untouched'       'OU=C,DC=x,OU=A,DC=x,OU=B,DC=x' (Order)
Check 'moving down works the same way'   $true $F::Move($s, 'x.net', (Ou 'OU=C,DC=x'), 1)
Check 'in the other direction'           'OU=A,DC=x,OU=C,DC=x,OU=B,DC=x' (Order)
Check 'and stops at the bottom'          $false $F::Move($s, 'x.net', (Ou 'OU=B,DC=x'), 1)
Check 'leaving the order untouched'      'OU=A,DC=x,OU=C,DC=x,OU=B,DC=x' (Order)

# A move is a swap with a neighbour. Anything else is a caller bug, not a bigger jump to honour.
Check 'a delta of zero is refused'       $false $F::Move($s, 'x.net', (Ou 'OU=B,DC=x'), 0)
Check 'and so is a jump of two'          $false $F::Move($s, 'x.net', (Ou 'OU=B,DC=x'), -2)
Check 'the order survives both'          'OU=A,DC=x,OU=C,DC=x,OU=B,DC=x' (Order)

# Nothing to move: an unpinned entry, an unknown domain, or no domain at all.
Check 'an unpinned entry cannot move'    $false $F::Move($s, 'x.net', (Ou 'OU=Z,DC=x'), -1)
# Both directions: moving an unpinned entry UP is refused by the lower bound whether or not it is found,
# so only moving one DOWN proves the not-found check is there.
Check 'in either direction'              $false $F::Move($s, 'x.net', (Ou 'OU=Z,DC=x'), 1)
Check 'nor can one in another domain'    $false $F::Move($s, 'other.net', (Ou 'OU=C,DC=x'), -1)
Check 'nor one with no domain'           $false $F::Move($s, '', (Ou 'OU=C,DC=x'), -1)
# Casing is not a different favourite, here as everywhere else.
Check 'a differently-cased entry moves'  $true  $F::Move($s, 'X.NET', (Ou 'ou=c,dc=x'), -1)
Check 'and lands where expected'         'OU=C,DC=x,OU=A,DC=x,OU=B,DC=x' (Order)

# The order is only worth storing if it comes back.
$dir = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-fav-" + [System.Guid]::NewGuid().ToString('N'))
$store = New-Object UnifiedDirectoryManager.Services.SettingsStore $dir
$store.Save($s)
$s = $store.Load()
Check 'the order survives a round trip'  'OU=C,DC=x,OU=A,DC=x,OU=B,DC=x' (Order)
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue

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

Write-Host "`n== pinning a saved search from the Advanced Search dialog ==" -ForegroundColor Cyan
# The dialog does not own the favourites; it is handed two hooks by whoever does. Test through the real view
# model, because the button's label and visibility are what an operator actually sees.
$searchDir = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-srch-" + [System.Guid]::NewGuid().ToString('N'))
$searchStore = New-Object UnifiedDirectoryManager.Services.SavedSearchStore $searchDir
foreach ($n in 'Disabled users', 'Stale accounts') {
    $sv = New-Object UnifiedDirectoryManager.Models.SavedSearch
    $sv.Name = $n
    $searchStore.Save($sv, $null)
}

# Stand in for MainViewModel's hooks with the same Favorites calls it makes.
$fs = NewSettings
$isPinned = [System.Func[string, bool]]{ param($name) $F::Contains($fs, 'contoso.net', (Search $name)) }
$toggled = New-Object System.Collections.Generic.List[string]
$togglePin = [System.Action[string]]{
    param($name)
    $toggled.Add($name)
    if ($F::Contains($fs, 'contoso.net', (Search $name))) { [void]$F::Remove($fs, 'contoso.net', (Search $name)) }
    else { [void]$F::Add($fs, 'contoso.net', (Search $name)) }
}
$pinning = New-Object UnifiedDirectoryManager.Services.SavedSearchPinning $isPinned, $togglePin

# IDialogService is only reached by save/delete confirmations, which this does not exercise.
$Vm = [UnifiedDirectoryManager.ViewModels.AdvancedSearchViewModel]
$vm = $Vm::new([UnifiedDirectoryManager.Services.IDialogService]$null, $searchStore, $pinning)
Check 'the dialog offers pinning'      $true  $vm.CanPinSearches
# The store seeds a couple of built-in searches the first time it is used, so look for the two added here
# rather than counting the lot.
$names = @($vm.SavedSearches | ForEach-Object { $_.Name })
Check 'the saved searches are listed' $true (($names -contains 'Disabled users') -and ($names -contains 'Stale accounts'))

$vm.SelectedSavedSearch = $vm.SavedSearches[0]
$name = $vm.SelectedSavedSearch.Name
Check 'nothing is pinned yet'          $false $vm.IsSelectedPinned
Check 'so the button offers to pin'    'Pin'  $vm.PinButtonText

$vm.TogglePinCommand.Execute($null)
Check 'pinning goes through the hook'  $true  ($toggled -contains $name)
Check 'and the search is now pinned'   $true  $F::Contains($fs, 'contoso.net', (Search $name))
Check 'the view model agrees'          $true  $vm.IsSelectedPinned
# The button has to change, or the only way to unpin is to guess that Pin now unpins.
Check 'and the button now offers Unpin' 'Unpin' $vm.PinButtonText

$vm.TogglePinCommand.Execute($null)
Check 'toggling again unpins'          $false $F::Contains($fs, 'contoso.net', (Search $name))
Check 'and the button reverts'         'Pin'  $vm.PinButtonText

# The button describes the SELECTED search. Move the selection and it has to be recomputed, or a pinned
# search shows Pin and an unpinned one shows Unpin.
[void]$F::Add($fs, 'contoso.net', (Search $vm.SavedSearches[1].Name))
$raisedVm = New-Object System.Collections.Generic.List[string]
$h = [System.ComponentModel.PropertyChangedEventHandler]{ param($s, $e) $raisedVm.Add($e.PropertyName) }
$vm.add_PropertyChanged($h)
$vm.SelectedSavedSearch = $vm.SavedSearches[1]
$vm.remove_PropertyChanged($h)
Check 'changing selection re-reads the pin' $true  $vm.IsSelectedPinned
Check 'and says so on the button'           'Unpin' $vm.PinButtonText
Check 'and the change is announced'         $true  ($raisedVm -contains 'PinButtonText')

# Nothing selected: there is no search to pin, so the command must not call the hook with an empty name.
$before = $toggled.Count
$vm.SelectedSavedSearch = $null
$vm.TogglePinCommand.Execute($null)
Check 'no selection pins nothing'      $before $toggled.Count
Check 'and reads as unpinned'          $false $vm.IsSelectedPinned

# No connection means no domain to file a favourite under, so the button is not offered at all.
$noPin = $Vm::new([UnifiedDirectoryManager.Services.IDialogService]$null, $searchStore, $null)
Check 'without hooks there is no button' $false $noPin.CanPinSearches
# It still must not throw when something calls it anyway.
$noPin.SelectedSavedSearch = $noPin.SavedSearches[0]
$noPin.TogglePinCommand.Execute($null)
Check 'and toggling is harmless'         $false $noPin.IsSelectedPinned
Remove-Item -Recurse -Force $searchDir -ErrorAction SilentlyContinue

Write-Host "`n== a favourite row never asks the directory for children (issue #8) ==" -ForegroundColor Cyan
# The Favourites row's distinguished name is the synthetic 'fav:root'; a pinned saved search's is
# 'fav:<name>'. Neither means anything to a domain controller, and 2.3.0 sent them anyway -- the DC replied
# 0000208F NameErr BAD_NAME, and the Children.Clear() that ran first left the pins gone.
#
# These nodes are built with a NULL IDirectoryService on purpose. That makes "did it call the directory"
# directly observable: if the guard holds, nothing is dereferenced and no error is raised; if it does not,
# the null reference throws, the catch swallows it, and the error handler fires. No fake is needed.
$errors = New-Object System.Collections.Generic.List[string]
$rec = [Action[string]] { param($m) $errors.Add($m) }
function Quiet { $script:errors.Clear() }

# "Did this node try to reach the directory?" is what these tests turn on, and with a null IDirectoryService
# an attempt cannot succeed. It surfaces one of two ways depending on the host:
#
#   - the null dereference is caught and reported through the error handler, or
#   - DirectoryService.Friendly() -- which the catch calls -- cannot load its LDAP assembly and the failure
#     escapes instead. PowerShell 7 ships System.DirectoryServices.Protocols 10.0.0.5, the app is built
#     against 10.0.0.9, and the loader binds its own copy whatever we do.
#
# Either outcome means the same thing, so Attempted treats them as one and the tests do not depend on which
# host they run on.
function Attempted($node) {
    $before = $script:errors.Count
    try { [void]$node.EnsureChildrenAsync().GetAwaiter().GetResult() }
    catch { return $true }
    return $script:errors.Count -gt $before
}
function Await($task) { [void]$task.GetAwaiter().GetResult() }

Quiet
$favRoot = $Node::new([UnifiedDirectoryManager.Models.FavoriteEntry]$null, 'Favourites', $null, $rec)
$favRoot.IsExpanded = $true
Check 'expanding the Favourites row calls nothing' 0 $errors.Count

Quiet
$pinnedOu = $Node::new((Ou 'OU=Sales,DC=contoso,DC=net'), 'Sales', $null, $rec)
$pinnedOu.IsExpanded = $true
Check 'nor does expanding a pinned OU'             0 $errors.Count

Quiet
$pinnedSearch = $Node::new((Search 'Disabled users'), 'Disabled users', $null, $rec)
$pinnedSearch.IsExpanded = $true
Check 'nor a pinned saved search'                  0 $errors.Count

# Refresh takes a different route: it calls Invalidate() -- which deliberately resets the once-only guard --
# and then EnsureChildrenAsync() directly. That path bypassed the expand handler entirely.
Quiet
$favRoot.Invalidate()
Check 'nor Refresh on the Favourites row'          $false (Attempted $favRoot)

Quiet
$pinnedSearch.Invalidate()
Check 'nor Refresh on a pinned saved search'       $false (Attempted $pinnedSearch)

Quiet
$pinnedOu.Invalidate()
Check 'nor Refresh on a pinned OU'                 $false (Attempted $pinnedOu)

Write-Host "`n== and the pins survive being collapsed and re-expanded ==" -ForegroundColor Cyan
# The visible half of the bug. EnsureChildrenAsync cleared Children BEFORE the call that failed, so the
# second expand emptied the row and nothing put the pins back until something rebuilt the favourites.
$favRoot.Children.Add($Node::new((Ou 'OU=A,DC=x'), 'A', $null, $rec))
$favRoot.Children.Add($Node::new((Ou 'OU=B,DC=x'), 'B', $null, $rec))
Check 'two pins to start with'          2 $favRoot.Children.Count
$favRoot.IsExpanded = $false
$favRoot.IsExpanded = $true
Check 'and both are still there'        2 $favRoot.Children.Count
$favRoot.Invalidate()
[void](Attempted $favRoot)
Check 'a Refresh does not empty it either' 2 $favRoot.Children.Count

Write-Host "`n== the guard is not over-broad ==" -ForegroundColor Cyan
# The negative control, and the one that matters most: a guard that blocked EVERY node would pass every
# check above and break the tree. A real container must still try to load, and with a null directory that
# attempt is exactly what raises an error.
Quiet
$ou = New-Object UnifiedDirectoryManager.Models.AdNode
$ou.DistinguishedName = 'OU=Real,DC=contoso,DC=net'
$ou.Name = 'Real'
$ou.Type = [UnifiedDirectoryManager.Models.AdObjectType]::OrganizationalUnit
$realOu = $Node::new($ou, [UnifiedDirectoryManager.Services.IDirectoryService]$null, $rec, $null, $false)
Check 'a real OU still tries to load'   $true (Attempted $realOu)

# And a failed load must leave what was already shown alone. Clearing before the call meant a domain
# controller hiccup read as "this OU is empty now" -- on every container, not only a favourite.
# A container starts with a single 'Loading...' placeholder child, so count from there. Note the
# consequence of not clearing up front: a first load that FAILS now leaves that placeholder in place
# rather than emptying the row. That is the better of the two lies -- 'not loaded' is true and re-expanding
# retries, where an empty row reads as 'this OU has nothing in it'.
Quiet
Check 'a fresh container shows a placeholder' $true $realOu.Children[0].IsPlaceholder
$realOu.Children.Add($Node::new((Ou 'OU=Child,DC=x'), 'Child', $null, $rec))
$realOu.Invalidate()
Check 'and it tried again'                   $true (Attempted $realOu)
# The real assertion: whatever was already on screen is still on screen after the failure.
Check 'a failed load keeps what was shown'   2 $realOu.Children.Count
Check 'including the real child'             'Child' $realOu.Children[1].Name

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
