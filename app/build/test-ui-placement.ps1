<#
.SYNOPSIS
  Tests the UI-placement work from docs/ui-placement-audit.md. Currently P1 (the cloud object list had no
  context menu), plus an invariant that applies to every view in the app.

.DESCRIPTION
  P1. Right-clicking a row in the on-prem list acts on it; right-clicking a row in the cloud list did
  nothing at all, because CloudObjectListView.xaml had no ContextMenu. One window, two object lists, two
  unrelated interaction models -- and the gesture a Windows administrator reaches for first was the dead
  one.

  The menu acts on the right-clicked row, falling back to the checked set when there is one, matching the
  on-prem list. The buttons above the list are deliberately NOT changed: they act strictly on what is
  checked, which is what the count beside them and their greying-out promise.

  The invariant is broader and is the reason this file is named for the audit rather than for P1. A
  mistyped command name in XAML -- {Binding EnabelSelectedCommand} -- fails SILENTLY: WPF finds no such
  property, the menu item greys out, and it looks exactly like a command that is legitimately unavailable.
  Nothing else in the build catches it. Every command binding in every view is checked here against the
  commands the view models actually expose.

  Run with:  pwsh -NoProfile -STA -File ./app/build/test-ui-placement.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dll = Join-Path $repoRoot 'debug\UnifiedDirectoryManager.dll'
$support = Join-Path $repoRoot 'debug\UnifiedDirectoryManager.TestSupport.dll'
if (-not (Test-Path $dll)) { throw "Build first — could not find $dll" }
if (-not (Test-Path $support)) { throw "Build the test-support project first — could not find $support" }
[System.Reflection.Assembly]::LoadFrom($dll) | Out-Null
[System.Reflection.Assembly]::LoadFrom($support) | Out-Null

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

$src = Join-Path $repoRoot 'app\src\UnifiedDirectoryManager'

Write-Host "`n== every command a view binds to actually exists ==" -ForegroundColor Cyan
# A mistyped binding greys the control out forever and looks identical to one that is simply unavailable.
# Nothing in the compiler or the XAML build catches it.
$defined = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($vmFile in (Get-ChildItem (Join-Path $src 'ViewModels') -Filter '*.cs' -Recurse)) {
    $text = Get-Content -Raw $vmFile.FullName
    foreach ($m in [regex]::Matches($text, '\[RelayCommand[^\]]*\]\s*(?:private|public)\s+(?:static\s+)?(?:async\s+)?(?:Task|void)\s+(\w+?)(Async)?\s*\(')) {
        [void]$defined.Add($m.Groups[1].Value + 'Command')
    }
    # A few commands are plain properties rather than generated, e.g. a row's own RunCommand.
    foreach ($m in [regex]::Matches($text, 'public\s+(?:I?[A-Za-z]*Command|IRelayCommand[^\s]*)\s+(\w+Command)\s*(?:\{|=>)')) {
        [void]$defined.Add($m.Groups[1].Value)
    }
}
Check 'the view models define commands' $true ($defined.Count -gt 20)

$bound = @{}
foreach ($view in (Get-ChildItem (Join-Path $src 'Views') -Filter '*.xaml' -Recurse)) {
    $text = Get-Content -Raw $view.FullName
    foreach ($m in [regex]::Matches($text, '\{Binding\s+(\w+Command)\s*\}')) {
        $name = $m.Groups[1].Value
        if (-not $bound.ContainsKey($name)) { $bound[$name] = @() }
        $bound[$name] += $view.Name
    }
}
Check 'the views bind to commands'      $true ($bound.Count -gt 20)

$unknown = @($bound.Keys | Where-Object { -not $defined.Contains($_) } | Sort-Object)
if ($unknown.Count -gt 0) {
    foreach ($u in $unknown) { Write-Host "          $u  (in $($bound[$u] -join ', '))" -ForegroundColor Yellow }
}
Check 'every bound command is defined'  0 $unknown.Count

# The check above matches \w+Command, so it never saw a DOTTED binding -- and P8 introduced four of
# them when File started reaching into the cloud list ({Binding Cloud.ExportAllCsvCommand}). A typo
# there fails exactly as silently as any other, so these are resolved for real: walk the property path
# on the built MainViewModel type and require the command to exist at the end of it.
# A dotted path is relative to the VIEW's own DataContext, so each view that uses one names its root
# here. A view that starts using dotted bindings without being listed fails the next assertion rather
# than being checked against the wrong type or skipped.
$dottedRoots = @{
    'MainWindow.xaml'     = 'MainViewModel'
    'SettingsWindow.xaml' = 'SettingsViewModel'
}
function VmType([string]$name) {
    [System.Type]::GetType("UnifiedDirectoryManager.ViewModels.$name, UnifiedDirectoryManager")
}
Check 'MainViewModel was loaded'       $true ($null -ne (VmType 'MainViewModel'))
$dotted = @{}
foreach ($view in (Get-ChildItem (Join-Path $src 'Views') -Filter '*.xaml' -Recurse)) {
    $text = Get-Content -Raw $view.FullName
    foreach ($m in [regex]::Matches($text, '\{Binding\s+((?:\w+\.)+\w+Command)\s*\}')) {
        $dotted[$m.Groups[1].Value] = $view.Name
    }
}
Check 'there are dotted bindings'       $true ($dotted.Count -ge 5)
$unlisted = @($dotted.Values | Sort-Object -Unique | Where-Object { -not $dottedRoots.ContainsKey($_) })
foreach ($u in $unlisted) { Write-Host "          $u uses dotted bindings and names no root" -ForegroundColor Yellow }
Check 'every such view names its root'  0 $unlisted.Count
$brokenPath = @()
foreach ($path in ($dotted.Keys | Sort-Object)) {
    $view = $dotted[$path]
    if (-not $dottedRoots.ContainsKey($view)) { continue }
    $type = VmType $dottedRoots[$view]
    foreach ($segment in ($path -split '\.')) {
        if ($null -eq $type) { break }
        $prop = $type.GetProperty($segment)
        $type = if ($null -eq $prop) { $null } else { $prop.PropertyType }
    }
    if ($null -eq $type) { $brokenPath += "$path  (in $view)" }
}
foreach ($b in $brokenPath) { Write-Host "          $b" -ForegroundColor Yellow }
Check 'every dotted binding resolves'   0 $brokenPath.Count
# Prove the walk can fail, rather than passing because GetProperty always returns something.
$cloudType = (VmType 'MainViewModel').GetProperty('Cloud').PropertyType
Check '  a bad path is caught'          $null ($cloudType.GetProperty('NoSuchCommand'))
Check '  and a good one is not'         $true ($null -ne $cloudType.GetProperty('ExportAllCsvCommand'))

Write-Host "`n== P1: the cloud list has a context menu ==" -ForegroundColor Cyan
$cloudXaml = Get-Content -Raw (Join-Path $src 'Views\Controls\CloudObjectListView.xaml')
Check 'the menu exists'                 $true ($cloudXaml -match '<ListView\.ContextMenu>')
foreach ($cmd in 'OpenSelectedCommand', 'EnableSelectedCommand', 'DisableSelectedCommand', 'RevokeSelectedCommand') {
    Check "  it offers $cmd"            $true ($cloudXaml -match [regex]::Escape("{Binding $cmd}"))
}
# Right-click has to select the row first, or the menu acts on whatever was selected beforehand. WPF does
# not do this on its own; the on-prem list has always carried the same handler.
Check 'right-click selects the row'     $true ($cloudXaml -match 'PreviewMouseRightButtonDown="OnListPreviewMouseRightButtonDown"')
$cloudCode = Get-Content -Raw (Join-Path $src 'Views\Controls\CloudObjectListView.xaml.cs')
Check 'and the handler exists'          $true ($cloudCode -match 'private void OnListPreviewMouseRightButtonDown')
# The user-only actions must not appear on the Groups or Devices lists.
$userItems = [regex]::Matches($cloudXaml, '<MenuItem Header="(Enable|Disable|Revoke)[^>]*>')
Check 'three user actions are offered'  3 $userItems.Count
foreach ($item in $userItems) {
    Check "  $(($item.Value -split '"')[1]) is users-only" $true ($item.Value -match 'ShowUserActions')
}

Write-Host "`n== P1: what a context action targets ==" -ForegroundColor Cyan
$ListVm = [UnifiedDirectoryManager.ViewModels.CloudObjectListViewModel]
$Row = [UnifiedDirectoryManager.Models.CloudObjectRow]
$Kind = [UnifiedDirectoryManager.Models.CloudObjectKind]

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-ui-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $sandbox | Out-Null

function New-CloudRow([string]$id, [string]$name) {
    $r = $Row::new()
    $Row.GetProperty('Id').SetValue($r, $id)
    $Row.GetProperty('DisplayName').SetValue($r, $name)
    $Row.GetProperty('Kind').SetValue($r, $Kind::User)
    return $r
}

try {
    $graph    = [UnifiedDirectoryManager.TestSupport.InertGraphService]::new()
    $exchange = [UnifiedDirectoryManager.TestSupport.InertExchangeService]::new()
    $dialogs  = [UnifiedDirectoryManager.TestSupport.InertDialogService]::new()
    $store    = [UnifiedDirectoryManager.Services.SettingsStore]::new($sandbox)
    $settings = [UnifiedDirectoryManager.Services.AppSettings]::new()
    $vm = $ListVm::new($graph, $exchange, $dialogs, $store, $settings)

    $a = New-CloudRow 'u-a' 'Amy'
    $b = New-CloudRow 'u-b' 'Bob'
    $c = New-CloudRow 'u-c' 'Cal'
    # The loader subscribes each row before adding it, which is what keeps CheckedCount in step with the
    # tick boxes. Adding rows straight to the collection here would skip that and quietly test a list the
    # app never builds, so the test attaches the very same handler.
    $onRowChanged = $ListVm.GetMethod('OnRowPropertyChanged', [System.Reflection.BindingFlags]'NonPublic,Instance')
    if ($null -eq $onRowChanged) { throw 'CloudObjectListViewModel has no OnRowPropertyChanged — has it been refactored?' }
    $handler = $onRowChanged.CreateDelegate([System.ComponentModel.PropertyChangedEventHandler], $vm)
    foreach ($r in $a, $b, $c) { $r.add_PropertyChanged($handler); $vm.Rows.Add($r) }

    Check 'nothing selected targets nothing'  0 (@($vm.ContextTargetRows)).Count

    # Right-clicking a row selects it, and the action follows the row rather than the checkboxes.
    $vm.SelectedRow = $b
    $target = @($vm.ContextTargetRows)
    Check 'the selected row is the target'    1 $target.Count
    Check 'and it is the right one'           'Bob' $target[0].DisplayName

    # Once anything is ticked, the checked set wins -- otherwise right-clicking inside a deliberate
    # multi-selection would quietly act on one row instead of all of them.
    $a.IsChecked = $true
    $c.IsChecked = $true
    $target = @($vm.ContextTargetRows)
    Check 'the checked set takes over'        2 $target.Count
    Check 'and it is the checked ones'        'Amy Cal' (($target | ForEach-Object { $_.DisplayName } | Sort-Object) -join ' ')
    Check 'not the merely selected row'       $false ($target.DisplayName -contains 'Bob')

    $a.IsChecked = $false
    $c.IsChecked = $false
    Check 'unticking hands it back'           1 (@($vm.ContextTargetRows)).Count

    Write-Host "`n== P1: the buttons are unchanged ==" -ForegroundColor Cyan
    # They promise "the checked users" in their tooltips and grey out with nothing ticked. A selected row
    # must not silently arm a bulk button.
    $vm.SelectedRow = $b
    Check 'a selected row does not arm Enable'  $false $vm.EnableCheckedCommand.CanExecute($null)
    Check 'nor Disable'                         $false $vm.DisableCheckedCommand.CanExecute($null)
    Check 'nor Revoke'                          $false $vm.RevokeCheckedCommand.CanExecute($null)
    # ...but the context action IS available, because that is what was right-clicked.
    Check 'while the context action is armed'   $true  $vm.EnableSelectedCommand.CanExecute($null)
    Check 'and Properties is too'               $true  $vm.OpenSelectedCommand.CanExecute($null)

    $b.IsChecked = $true
    Check 'ticking arms the bulk buttons'       $true $vm.EnableCheckedCommand.CanExecute($null)

    Write-Host "`n== P1: only users, and only in the Users list ==" -ForegroundColor Cyan
    $vm.Mode = [UnifiedDirectoryManager.Services.CloudListMode]::Groups
    Check 'the Groups list offers no account actions' $false $vm.EnableSelectedCommand.CanExecute($null)
    Check 'but Properties still opens'                $true  $vm.OpenSelectedCommand.CanExecute($null)
}
finally { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "`n== P1: one implementation, two entry points (mutation check) ==" -ForegroundColor Cyan
# The work must not be copied per entry point. That is how Copy User's token resolver drifted from New
# User's (F26) and wrote a different UPN for the same template.
$vmSrc = Get-Content -Raw (Join-Path $src 'ViewModels\CloudObjectListViewModel.cs')
$runs = ([regex]::Matches($vmSrc, 'private async Task RunBulkAsync')).Count
Check 'there is one RunBulkAsync'       1 $runs
Check 'it takes its targets'            $true ($vmSrc -match 'RunBulkAsync\(string verb, Func<CloudObjectRow, Task> action, IReadOnlyList<CloudObjectRow> targets\)')
$graphCalls = ([regex]::Matches($vmSrc, 'SetUserAccountEnabledAsync|RevokeSignInSessionsAsync')).Count
Check 'the six commands are thin'       6 $graphCalls
Check 'the context rule is one property' 1 ([regex]::Matches($vmSrc, 'public IReadOnlyList<CloudObjectRow> ContextTargetRows')).Count

Write-Host "`n== P2/P3: the tree's actions exist in the menu bar ==" -ForegroundColor Cyan
# Favourites and OU management were reachable ONLY by right-clicking a tree node. Pinning is a whole
# feature that an operator who never tried that gesture had no way of discovering.
$mainXaml = Get-Content -Raw (Join-Path $src 'Views\MainWindow.xaml')
$menuOnly = $mainXaml.Substring($mainXaml.IndexOf('<Menu'), $mainXaml.IndexOf('</Menu>') - $mainXaml.IndexOf('<Menu'))

foreach ($cmd in 'PinSelectedNodeCommand', 'UnpinSelectedNodeCommand',
                 'MoveSelectedFavoriteUpCommand', 'MoveSelectedFavoriteDownCommand') {
    Check "  the menu bar offers $cmd" $true ($menuOnly -match [regex]::Escape("{Binding $cmd}"))
}
foreach ($cmd in 'CreateOuHereCommand', 'SelectedNodePropertiesCommand', 'DeleteSelectedOuCommand') {
    Check "  the menu bar offers $cmd" $true ($menuOnly -match [regex]::Escape("{Binding $cmd}"))
}

# A menu bar is an index. An item that DISAPPEARS when unavailable teaches that the feature does not
# exist, which is the lesson this whole audit is undoing -- so these must be enabled/disabled, never
# shown/hidden. The context menu is the opposite and correctly hides what does not apply.
$favMenu = [regex]::Match($menuOnly, '(?s)<MenuItem Header="_Favourites">.*?</MenuItem>\s*<MenuItem Header="Selected F_older">').Value
Check 'the Favourites submenu was found'  $true ($favMenu.Length -gt 0)
Check 'and hides nothing'                 $false ($favMenu -match 'Visibility=')
$folderMenu = [regex]::Match($menuOnly, '(?s)<MenuItem Header="Selected F_older">.*?</MenuItem>\s*</MenuItem>').Value
Check 'the Folder submenu was found'      $true ($folderMenu.Length -gt 0)
Check 'and hides nothing either'          $false ($folderMenu -match 'Visibility=')

# Right-click must select the tree node, or the context menu and the View menu disagree about which
# folder they mean with nothing on screen to say so. Both object lists already did this; the tree did not.
Check 'right-click selects a tree node'   $true ($mainXaml -match 'PreviewMouseRightButtonDown="OnNodePreviewMouseRightButtonDown"')
$mainCode = Get-Content -Raw (Join-Path $src 'Views\MainWindow.xaml.cs')
Check 'and the handler exists'            $true ($mainCode -match 'private void OnNodePreviewMouseRightButtonDown')

Write-Host "`n== P2/P3: the commands follow the tree selection ==" -ForegroundColor Cyan
# The methods behind these have always existed; what was missing was a command surface over the
# SELECTED node, so a menu-bar item could act on it.
$MainVm = [UnifiedDirectoryManager.ViewModels.MainViewModel]
foreach ($cmd in 'PinSelectedNodeCommand', 'UnpinSelectedNodeCommand', 'MoveSelectedFavoriteUpCommand',
                 'MoveSelectedFavoriteDownCommand', 'CreateOuHereCommand', 'SelectedNodePropertiesCommand',
                 'DeleteSelectedOuCommand') {
    Check "  $cmd exists" $true ($null -ne $MainVm.GetProperty($cmd))
}

# The gates are public so the menu can bind them, and they all read through SelectedNode -- which is
# null until something is selected, and must not throw then.
foreach ($gate in 'CanPinSelectedNode', 'SelectedNodeIsFavorite', 'CanCreateOuHere', 'SelectedNodeIsOu') {
    $prop = $MainVm.GetProperty($gate)
    Check "  $gate exists" $true ($null -ne $prop)
}

$vmSrc = Get-Content -Raw (Join-Path $src 'ViewModels\MainViewModel.cs')
# Every gate reads the selection through ?. so nothing selected is false rather than an exception.
foreach ($gate in 'CanPinSelectedNode', 'SelectedNodeIsFavorite', 'CanCreateOuHere', 'SelectedNodeIsOu') {
    $line = [regex]::Match($vmSrc, '(?m)^\s*public bool ' + $gate + ' =>.*$').Value
    Check "  $gate tolerates no selection" $true ($line -match 'SelectedNode\?\.')
}

# And the availability has to move WITH the selection, including when it is cleared or moves to a cloud
# node -- so the notify has to run before the early returns in the selection hook, not after them.
$hook = [regex]::Match($vmSrc, '(?s)partial void OnSelectedNodeChanged\(TreeNodeViewModel\? value\)\s*\{.*?\r?\n    \}').Value
Check 'the selection hook was found'      $true ($hook.Length -gt 0)
Check 'it re-evaluates the commands'      $true ($hook -match 'NotifyNodeCommands\(\)')
Check 'before any early return'           $true ($hook.IndexOf('NotifyNodeCommands()') -lt $hook.IndexOf('if (value is null) return;'))

# One implementation: the commands delegate to the methods the context menu already calls.
foreach ($pair in @(@('PinSelectedNode', 'PinNode'), @('UnpinSelectedNode', 'UnpinNode'),
                    @('CreateOuHereAsync', 'CreateOuUnderAsync'), @('SelectedNodeProperties', 'ShowNodeProperties'),
                    @('DeleteSelectedOuAsync', 'DeleteOuAsync'))) {
    $body = [regex]::Match($vmSrc, '(?m)^\s*private (?:void|Task) ' + $pair[0] + '\(\).*$').Value
    Check "  $($pair[0]) delegates to $($pair[1])" $true ($body -match ([regex]::Escape($pair[1]) + '\(SelectedNode'))
}
Write-Host "`n== P4: the right-click-only actions are in the menu bar ==" -ForegroundColor Cyan
# Enable, Disable and Unlock are among the most-used actions in a directory tool and appeared nowhere in
# the menu bar at all.
foreach ($cmd in 'EnableSelectedCommand', 'DisableSelectedCommand', 'UnlockSelectedCommand',
                 'MoveSelectedToOuCommand', 'SaveSelectedAsTemplateCommand',
                 'ExportGroupMembersCommand', 'AppendGroupMembersCommand') {
    Check "  the menu bar offers $cmd" $true ($menuOnly -match [regex]::Escape("{Binding $cmd}"))
}

Write-Host "`n== P5: the menu-only actions are on right-click ==" -ForegroundColor Cyan
$listXaml = Get-Content -Raw (Join-Path $src 'Views\Controls\ObjectListView.xaml')
# A per-user action whose first natural home is the user's own right-click menu.
Check 'Copy groups to user is offered'  $true ($listXaml -match [regex]::Escape('{Binding CopyGroupsToUserCommand}'))
Check 'and only for users'              $true ([regex]::Match($listXaml, '<MenuItem Header="Copy groups to user[^/]*/>').Value -match 'SelectionHasUsers')

# The tree's three create entries described one feature three ways. "Here" belongs on the two that
# create INSIDE the selected container and not on the cloud one, which has no container to create in.
# "Create OU Here…" became "New OU Here…" when P8 moved it under Action > New, so that all three read
# as the same kind of thing as their menu-bar entries.
Check 'New OU Here is title case'       $true ($mainXaml -match 'Header="New OU Here…"')
Check 'New Group Here matches it'       $true ($mainXaml -match 'Header="New Group Here…"')
Check 'New Cloud Group has no "Here"'   $true ($mainXaml -match 'Header="New Cloud Group…"')

Write-Host "`n== P4: menu items disable rather than vanish ==" -ForegroundColor Cyan
# The whole point of rule 1. An item that disappears when it does not apply teaches that the feature does
# not exist, so every one of these has to be gated by CanExecute rather than by Visibility.
$vmSrc2 = Get-Content -Raw (Join-Path $src 'ViewModels\MainViewModel.cs')
$gates = @{
    'EnableSelectedAsync'       = 'SelectionHasDisabled'
    'DisableSelectedAsync'      = 'SelectionHasEnabled'
    'UnlockSelectedAsync'       = 'SelectionHasUsers'
    'MoveSelectedToOuAsync'     = 'HasSelection'
    'SaveSelectedAsTemplate'    = 'SelectionHasUsers'
    'CopyUser'                  = 'SelectionHasUsers'
    'CopyGroupsToUser'          = 'SelectionHasUsers'
    'ExportGroupMembersAsync'   = 'SelectionHasGroups'
    'AppendGroupMembersAsync'   = 'SelectionHasGroups'
    'ResetPasswordSelectedAsync' = 'SelectionIsOneUser'
}
foreach ($method in ($gates.Keys | Sort-Object)) {
    $want = $gates[$method]
    $decl = [regex]::Match($vmSrc2, '\[RelayCommand\(CanExecute = nameof\((\w+)\)\)\]\s*(?:private|public)[^\n]*\b' + [regex]::Escape($method) + '\(')
    Check "  $method is gated on $want" $want ($decl.Groups[1].Value)
}

# None of these may hide themselves. They lived in Edit when P4 put them in the menu bar; P8 moved
# them to Action, and the assertion follows rather than being quietly dropped.
$actionMenu = [regex]::Match($menuOnly, '(?s)<MenuItem Header="_Action" Visibility="\{Binding IsAdView.*?\n            </MenuItem>').Value
Check 'the AD Action menu was found'    $true ($actionMenu.Length -gt 0)
foreach ($cmd in 'EnableSelectedCommand', 'DisableSelectedCommand', 'UnlockSelectedCommand',
                 'MoveSelectedToOuCommand', 'SaveSelectedAsTemplateCommand', 'ResetPasswordSelectedCommand') {
    $item = [regex]::Match($actionMenu, '<MenuItem[^/]*' + $cmd + '[^/]*/>').Value
    Check "  $cmd is never hidden" $false ($item -match 'Visibility=')
}

# The gates only mean anything if they are re-evaluated when the selection changes.
$sel = [regex]::Match($vmSrc2, '(?s)private void UpdateSelectionState\(\).*?\r?\n    \}').Value
Check 'UpdateSelectionState was found'  $true ($sel.Length -gt 0)
foreach ($cmd in 'EnableSelectedCommand', 'DisableSelectedCommand', 'UnlockSelectedCommand',
                 'MoveSelectedToOuCommand', 'SaveSelectedAsTemplateCommand', 'CopyUserCommand',
                 'CopyGroupsToUserCommand', 'ExportGroupMembersCommand', 'AppendGroupMembersCommand',
                 'ResetPasswordSelectedCommand') {
    Check "  it re-evaluates $cmd" $true ($sel -match ([regex]::Escape($cmd) + '\.NotifyCanExecuteChanged\(\)'))
}

Write-Host "`n== rule 1: the menu bar is complete ==" -ForegroundColor Cyan
# The invariant the whole first work package exists to establish. Every command MainViewModel exposes
# must be reachable from the menu bar -- otherwise the menu bar is a subset, and a subset teaches "if it
# is not here it does not exist", which is false and is what sent this audit off in the first place.
#
# A handful of commands are genuinely not menu-bar material and are named here rather than silently
# skipped, so that the exception list is something a reader can argue with.
# As of P6 this list is EMPTY -- every command really is there. Keep the mechanism: the next command
# that genuinely does not belong in a menu goes here with its reason, not into a silent skip.
$notInMenuBar = @()
$mainCommands = @()
foreach ($m in [regex]::Matches($vmSrc2, '\[RelayCommand[^\]]*\]\s*(?:private|public)\s+(?:async\s+)?(?:Task|void)\s+(\w+?)(Async)?\s*\(')) {
    $mainCommands += ($m.Groups[1].Value + 'Command')
}
$mainCommands = @($mainCommands | Sort-Object -Unique)
Check 'MainViewModel exposes commands'  $true ($mainCommands.Count -ge 30)

$missing = @()
foreach ($cmd in $mainCommands) {
    if ($notInMenuBar -contains $cmd) { continue }
    if (-not ($menuOnly -match [regex]::Escape("{Binding $cmd}"))) { $missing += $cmd }
}
if ($missing.Count -gt 0) { foreach ($m in $missing) { Write-Host "          $m" -ForegroundColor Yellow } }
Check 'every command is in the menu bar' 0 $missing.Count
Write-Host "`n== P6: one command, one label ==" -ForegroundColor Cyan
# The audit's complaint, in one sentence: an operator who saw "Templates…" on the toolbar went looking
# for it in the menus and found "User Creation Templates…", because you cannot scan a menu for a word
# that is not the one the item starts with.
#
# Labels are compared with the accelerator underscore removed -- "_Refresh" and "Refresh" are one label.
function LabelText([string]$s) { ($s -replace '_', '') -replace '…', '...' }
# The leading comma is load-bearing. Without it PowerShell unrolls a one-word label to a bare string,
# and $words[0] then indexes that string by CHARACTER: 'delete' compares as 'd', so every label with a
# single word silently fails the comparison below.
function LabelWords([string]$s) { return ,@(($s.ToLowerInvariant() -split '[^a-z0-9()]+') | Where-Object { $_ }) }

# Pull (label, command) out of a region of XAML. Attribute order varies, so both orders are handled.
function Region([string]$xml, [string]$openTag, [string]$closeTag) {
    $a = $xml.IndexOf($openTag); $b = $xml.IndexOf($closeTag)
    if ($a -lt 0 -or $b -le $a) { return '' }
    return $xml.Substring($a, $b - $a)
}

function BoundLabels([string]$xml) {
    $out = @{}
    $path = New-Object System.Collections.Stack   # headers of the open MenuItems, outermost last
    foreach ($m in [regex]::Matches($xml, '<(?:MenuItem|Button)[\s][^>]*?(/?)>|</MenuItem>')) {
        if ($m.Value -eq '</MenuItem>') { if ($path.Count -gt 0) { [void]$path.Pop() }; continue }
        $lab = [regex]::Match($m.Value, '(?:Header|Content)="([^"]*)"')
        $cmd = [regex]::Match($m.Value, 'Command="\{Binding ([A-Za-z0-9_.]+)\}"')
        $own = if ($lab.Success) { LabelText $lab.Groups[1].Value } else { '' }
        if ($lab.Success -and $cmd.Success) {
            $key = $cmd.Groups[1].Value -replace '^Cloud\.', ''   # the cloud pane's own DataContext
            if (-not $out.ContainsKey($key)) { $out[$key] = @() }
            $out[$key] += $own
            # Prefixed with the enclosing submenu, but never with a top-level menu name: "New" is part
            # of the item's name, "Action" is not.
            if ($path.Count -ge 2) { $out[$key] += ((LabelText $path.Peek()) + ' ' + $own) }
        }
        if ($m.Value -notmatch '^<Button' -and $m.Groups[1].Value -ne '/') { $path.Push($own) }
    }
    return $out
}

$toolbarXml = Region $mainXaml '<ToolBarTray' '</ToolBarTray>'
$menuLabels    = BoundLabels $menuOnly
$listCtxXml    = Region $listXaml '<ContextMenu' '</ContextMenu>'
$listLabels    = BoundLabels $listCtxXml

Check 'the list context menu was found' $true ($listLabels.Count -ge 8)

# The toolbar is not allowed its own vocabulary -- it is a shortcut to a menu item, so it says what
# that menu item says. Since T1 the toolbar is built from a catalogue rather than written out in XAML,
# so that check lives in the T1 block below and covers every button that CAN be added, not just the
# ones on it today.

# --- the context menu may drop words, never change them ---------------------------------------------
# "Delete…" for "Delete Selected…" is fine: you right-clicked the selection, so the menu need not say
# what the gesture already said. "Modify…" for "Open Selected…" was not fine -- it is a different word,
# so nothing connects the two surfaces. The rule: the context label's words appear in the menu label, in
# order, and the first word is the same, so both sort to the same place in an operator's head.
# One word may be ADDED rather than dropped: a tree context menu says "New Group Here…" because it was
# opened on the container being created in, which the menu bar has no way to name. It is listed rather
# than allowed generally, because "the context menu may add words" is not a rule, it is the absence of
# one.
$ContextOnlyWords = @('here')
function IsShorteningOf([string]$short, [string]$long) {
    $s = @((LabelWords $short) | Where-Object { $ContextOnlyWords -notcontains $_ })
    $l = LabelWords $long
    if ($s.Count -eq 0 -or $l.Count -eq 0) { return $false }
    if ($s[0] -ne $l[0]) { return $false }
    $i = 0
    foreach ($w in $l) { if ($i -lt $s.Count -and $w -eq $s[$i]) { $i++ } }
    return ($i -eq $s.Count)
}
Check '  a shortening is accepted'      $true  (IsShorteningOf 'Delete...' 'Delete Selected...')
Check '  dropped interior words too'    $true  (IsShorteningOf 'Export members to CSV...' 'Export Group Members to CSV...')
Check '  a different word is not'       $false (IsShorteningOf 'Modify...' 'Open Selected...')
Check '  nor a different first word'    $false (IsShorteningOf 'Selected Delete...' 'Delete Selected...')
Check '  nor reordered words'           $false (IsShorteningOf 'CSV to members export' 'Export members to CSV')

$ctxDrift = @()
foreach ($cmd in ($listLabels.Keys | Sort-Object)) {
    if (-not $menuLabels.ContainsKey($cmd)) { continue }
    $c = $listLabels[$cmd][0]
    $ok = $false
    foreach ($m in $menuLabels[$cmd]) { if (IsShorteningOf $c $m) { $ok = $true } }
    if (-not $ok) { $ctxDrift += ($cmd + ": context '" + $c + "' vs menu '" + $menuLabels[$cmd][0] + "'") }
}
foreach ($d in $ctxDrift) { Write-Host "          $d" -ForegroundColor Yellow }
Check 'every context label shortens its menu label' 0 $ctxDrift.Count

# --- the three the audit named, and the two P4 added ------------------------------------------------
Check '  no "Export CSV" button survives'  $false ($mainXaml -match 'Content="Export CSV…"')
Check '  no "Bulk Create" button survives' $false ($mainXaml -match 'Content="Bulk Create…"')
Check '  no bare "Templates" label'        $false ($mainXaml -match '(?:Content|Header)="_?Templates…"')
Check '  no bare "Logs" button'            $false ($mainXaml -match 'Content="Logs"')
Check '  no "Modify" anywhere'             $false ($listXaml -match 'Header="Modify…"')
Check '  no "a CSV"'                       $false ($listXaml -match 'to a CSV')

# --- the tree context menu and the View menu are the same feature -----------------------------------
# Scoped to the TreeView: the toolbar has a context menu of its own now, and it appears earlier in the
# file, so searching the whole document finds that one instead and every assertion below quietly moves
# to a menu it was not written for.
$treeRegion = Region $mainXaml '<TreeView' '</TreeView>'
Check 'the tree was found'              $true ($treeRegion.Length -gt 0)
$treeCtx = Region $treeRegion '<ContextMenu' '</ContextMenu>'
Check 'the tree context menu was found' $true ($treeCtx.Length -gt 200)
foreach ($pair in @(@('Pin to Favourites', 'PinSelectedNodeCommand'),
                    @('Unpin from Favourites', 'UnpinSelectedNodeCommand'))) {
    Check ("  the tree says " + $pair[0]) $true ($treeCtx -match [regex]::Escape('Header="' + $pair[0] + '"'))
    Check  "  and so does the View menu"  $true ((LabelText ($menuLabels[$pair[1]][0])) -eq $pair[0])
}
# It opens a window, so it takes an ellipsis, like every other item in the app that opens one.
Check '  the tree Properties has an ellipsis' $true ($treeCtx -match 'Header="Properties…"')

# The tree's menu is wired with Click= handlers, so BoundLabels cannot see it and the label rules
# above never reach it -- the same blind spot that let Favourites and OU management go unnoticed until
# P2 and P3. Each of its items is therefore checked BY NAME above. This counts them, so that adding an
# eleventh without adding an assertion for it fails here rather than sliding in unexamined.
$treeItems = @()
foreach ($m in [regex]::Matches($treeCtx, '<MenuItem[\s][^>]*Header="([^"]*)"')) { $treeItems += $m.Groups[1].Value }
$treeCovered = @('New OU Here…', 'New Group Here…', 'New Cloud Group…', 'Pin to Favourites',
                 'Unpin from Favourites', 'Move up', 'Move down', 'Properties…', 'Delete OU',
                 "Yes, I'm sure…")
$treeUncovered = @($treeItems | Where-Object { $treeCovered -notcontains $_ })
foreach ($u in $treeUncovered) { Write-Host "          tree item '$u' has no assertion" -ForegroundColor Yellow }
Check '  every tree item is accounted for' 0 $treeUncovered.Count
Check '  and none went missing'            $treeCovered.Count $treeItems.Count

Write-Host "`n== P6: no menu offers one letter twice ==" -ForegroundColor Cyan
# A duplicate accelerator still works -- WPF cycles through the matches -- but Alt+D landing on Disable
# or on Delete depending on how many times you press it is not a keyboard shortcut, it is a coin toss.
# Siblings are found by tracking nesting, not indentation: two different submenus sit at the same depth
# and their letters are allowed to collide.
function SiblingGroups([string]$xml) {
    $groups = New-Object System.Collections.ArrayList
    $root = New-Object System.Collections.ArrayList
    $stack = New-Object System.Collections.Stack
    $stack.Push($root); [void]$groups.Add($root)
    foreach ($m in [regex]::Matches($xml, '<MenuItem[\s][^>]*?(/?)>|</MenuItem>')) {
        if ($m.Value -eq '</MenuItem>') { if ($stack.Count -gt 1) { [void]$stack.Pop() }; continue }
        $h = [regex]::Match($m.Value, 'Header="([^"]*)"')
        if ($h.Success) { [void]($stack.Peek()).Add($h.Groups[1].Value) }
        if ($m.Groups[1].Value -ne '/') {
            $kids = New-Object System.Collections.ArrayList
            [void]$groups.Add($kids); $stack.Push($kids)
        }
    }
    return ,$groups   # same reason as LabelWords: do not let the outer list unroll
}
$groups = SiblingGroups $menuOnly
Check 'the menu tree was walked'        $true ($groups.Count -ge 7)
$dupes = @()
foreach ($g in $groups) {
    $byLetter = @{}
    foreach ($h in $g) {
        $k = [regex]::Match($h, '_(.)')
        if (-not $k.Success) { continue }
        $letter = $k.Groups[1].Value.ToUpperInvariant()
        if (-not $byLetter.ContainsKey($letter)) { $byLetter[$letter] = @() }
        # Two siblings with the SAME header are one item shown two ways -- the AD and cloud Action
        # menus, which are mutually exclusive by view. A letter is only ambiguous when it could mean
        # two different things.
        if ($byLetter[$letter] -notcontains $h) { $byLetter[$letter] += $h }
    }
    foreach ($letter in ($byLetter.Keys | Sort-Object)) {
        if ($byLetter[$letter].Count -gt 1) {
            $dupes += ("Alt+" + $letter + " means " + ($byLetter[$letter] -join ' AND '))
        }
    }
}
foreach ($d in $dupes) { Write-Host "          $d" -ForegroundColor Yellow }
Check 'no menu repeats an accelerator'  0 $dupes.Count
# Prove the walker separates siblings from cousins, rather than passing because it found nothing.
$probe = SiblingGroups '<MenuItem Header="_A"><MenuItem Header="_X" /><MenuItem Header="_Y" /></MenuItem><MenuItem Header="_B"><MenuItem Header="_X" /></MenuItem>'
Check '  it groups by nesting'          3 $probe.Count
Check '  cousins may share a letter'    2 (@($probe | Where-Object { $_ -contains '_X' }).Count)
# Same letter, same header: one item shown two ways. Same letter, different headers: a coin toss.
function AccelClash([string[]]$headers) {
    $byLetter = @{}
    foreach ($h in $headers) {
        $k = [regex]::Match($h, '_(.)'); if (-not $k.Success) { continue }
        $l = $k.Groups[1].Value.ToUpperInvariant()
        if (-not $byLetter.ContainsKey($l)) { $byLetter[$l] = @() }
        if ($byLetter[$l] -notcontains $h) { $byLetter[$l] += $h }
    }
    foreach ($l in $byLetter.Keys) { if ($byLetter[$l].Count -gt 1) { return $true } }
    return $false
}
Check '  twins are not a clash'         $false (AccelClash @('_Action', '_Action'))
Check '  but two real items are'        $true  (AccelClash @('_Action', '_Advanced Search…'))

Write-Host "`n== P7: nothing is in the menu bar twice ==" -ForegroundColor Cyan
# Refresh was in File, in View and on the toolbar; the two log commands were in File and in Help, word
# for word. A command in two menus does not make it easier to find -- it makes the menu bar look like it
# holds more than it does, and it means neither menu is the answer to "where does this live?".
foreach ($cmd in 'RefreshCommand', 'ViewLogCommand', 'OpenLogsCommand') {
    Check ("  " + $cmd + " is in the menu bar once") 1 ([regex]::Matches($menuOnly, [regex]::Escape("{Binding $cmd}")).Count)
}
$fileMenu = [regex]::Match($menuOnly, '(?s)<MenuItem Header="_File">.*?\n            </MenuItem>').Value
$viewMenu = [regex]::Match($menuOnly, '(?s)<MenuItem Header="_View">.*?\n            </MenuItem>').Value
$helpMenu = [regex]::Match($menuOnly, '(?s)<MenuItem Header="_Help">.*?\n            </MenuItem>').Value
Check 'the File menu was found'         $true ($fileMenu.Length -gt 0)
Check 'the View menu was found'         $true ($viewMenu.Length -gt 0)
Check 'the Help menu was found'         $true ($helpMenu.Length -gt 0)
# Refresh belongs with the view it refreshes; the logs belong with the other "what did it do" items.
Check '  Refresh is in View'            $true  ($viewMenu -match 'RefreshCommand')
Check '  and not in File'               $false ($fileMenu -match 'RefreshCommand')
Check '  the logs are in Help'          $true  (($helpMenu -match 'ViewLogCommand') -and ($helpMenu -match 'OpenLogsCommand'))
Check '  the logs are not in File'      $false (($fileMenu -match 'ViewLogCommand') -or ($fileMenu -match 'OpenLogsCommand'))
# Removing them must not leave two separators touching, which draws a line across an empty gap.
Check '  File has no doubled separator' $false ($fileMenu -match '<Separator[^>]*/>\s*<Separator')
Check '  File does not end on one'      $false ($fileMenu -match '<Separator[^>]*/>\s*</MenuItem>')

# The toolbar is allowed to duplicate -- that is what rule 3 says it is for -- so this is not a
# regression of P7, and saying so here stops someone "fixing" it later. Since T1 the question is about
# the catalogue rather than the XAML, and it is asserted in the T1 block below.

Write-Host "`n== P8: the menu bar is laid out the way MMC lays one out ==" -ForegroundColor Cyan
# File / Action / View / Tools / Help, with New as a submenu inside Action. The previous shape had four
# creates in File and a fifth nowhere, and split actions on the selection across File, Edit and Tools,
# which is the structural reason the other findings existed.
$topLevel = @()
foreach ($m in [regex]::Matches($menuOnly, '(?m)^            <MenuItem Header="([^"]*)"')) {
    $topLevel += (LabelText $m.Groups[1].Value)
}
Check 'the top-level menus are as agreed' 'File|Action|Action|View|Tools|Help' ($topLevel -join '|')
# Two Action menus, not one with per-item Visibility: IsAdView and IsCloudView are strict complements
# (IsAdView => !IsCloudView), so exactly one is ever on screen.
Check '  IsAdView is the complement'     $true ($vmSrc2 -match 'public bool IsAdView => !IsCloudView')
$adAction    = [regex]::Match($menuOnly, '(?s)<MenuItem Header="_Action" Visibility="\{Binding IsAdView.*?\n            </MenuItem>').Value
$cloudAction = [regex]::Match($menuOnly, '(?s)<MenuItem Header="_Action" Visibility="\{Binding IsCloudView.*?\n            </MenuItem>').Value
Check '  an AD Action menu exists'       $true ($adAction.Length -gt 0)
Check '  a cloud Action menu exists'     $true ($cloudAction.Length -gt 0)
Check '  Edit is gone'                   $false ($menuOnly -match '<MenuItem Header="_Edit"')
foreach ($a in $adAction, $cloudAction) {
    Check '  Action opens with New'      $true ($a -match '<MenuItem Header="_New">')
}

# Every action on the selected object is in Action, and nowhere else in the menu bar. This is the test
# the finding asked for: one menu to open, and everything in it.
$selectionCommands = @(
    'OpenSelectedCommand', 'EnableSelectedCommand', 'DisableSelectedCommand', 'UnlockSelectedCommand',
    'ResetPasswordSelectedCommand', 'CopyUserCommand', 'CopyGroupsToUserCommand',
    'SaveSelectedAsTemplateCommand', 'AddSelectedToGroupsCommand', 'MoveSelectedToOuCommand',
    'BulkEditCommand', 'ExportGroupMembersCommand', 'AppendGroupMembersCommand', 'DeleteSelectedCommand'
)
$strays = @()
foreach ($cmd in $selectionCommands) {
    if (-not ($adAction -match [regex]::Escape("{Binding $cmd}"))) { $strays += "$cmd is not in Action" }
    $everywhere = [regex]::Matches($menuOnly, [regex]::Escape("{Binding $cmd}")).Count
    if ($everywhere -ne 1) { $strays += "$cmd appears $everywhere times in the menu bar" }
}
foreach ($s in $strays) { Write-Host "          $s" -ForegroundColor Yellow }
Check 'one menu holds the selection actions' 0 $strays.Count

# The list exports write out what you are LOOKING at; the group-member exports write out what you
# PICKED. That is the line between File and Action, and it is the only reason they are apart.
$fileMenu2 = [regex]::Match($menuOnly, '(?s)<MenuItem Header="_File">.*?\n            </MenuItem>').Value
Check '  File exports the list'          $true  ($fileMenu2 -match 'ExportCsvCommand')
Check '  and the cloud list, both ways'  $true  (($fileMenu2 -match 'Cloud\.ExportCsvCommand') -and ($fileMenu2 -match 'Cloud\.ExportAllCsvCommand'))
Check '  Action exports the selection'   $true  ($adAction -match 'ExportGroupMembersCommand')
Check '  File does not'                  $false ($fileMenu2 -match 'ExportGroupMembersCommand')

# Creating an OU joined the other creates; the two items that act on the tree node did not, because a
# menu that means "the row I picked" or "the folder I picked" depending on the pane is not learnable.
Check '  New offers OU'                  $true  ($adAction -match 'CreateOuHereCommand')
$viewMenu2 = [regex]::Match($menuOnly, '(?s)<MenuItem Header="_View">.*?\n            </MenuItem>').Value
Check '  View keeps OU properties'       $true  ($viewMenu2 -match 'SelectedNodePropertiesCommand')
Check '  View keeps Delete OU'           $true  ($viewMenu2 -match 'DeleteSelectedOuCommand')
Check '  Action has neither'             $false (($adAction -match 'SelectedNodePropertiesCommand') -or ($adAction -match 'DeleteSelectedOuCommand'))

Write-Host "`n== P8: the menu bar hides by VIEW, never by selection ==" -ForegroundColor Cyan
# Hiding by view is honest: a cloud user has no lockout to clear, so Unlock is not part of the cloud
# vocabulary at all. Hiding by anything else teaches "if it is not here it does not exist", which is the
# lesson this whole audit is undoing -- selection gates items with CanExecute so they grey out instead.
$badVis = @()
foreach ($m in [regex]::Matches($menuOnly, '<MenuItem[\s][^>]*?Visibility="\{Binding ([A-Za-z0-9_.]+)[,}]')) {
    if ($m.Groups[1].Value -notin 'IsAdView', 'IsCloudView') { $badVis += $m.Groups[1].Value }
}
foreach ($b in ($badVis | Sort-Object -Unique)) { Write-Host "          Visibility bound to $b" -ForegroundColor Yellow }
Check 'menu items hide only by view'    0 $badVis.Count
# A separator that hides has to hide with the block it separates, or it draws a line across a gap.
Check '  and so do separators'          $true ($menuOnly -notmatch '<Separator[^>]*Visibility="\{Binding (?!IsAdView|IsCloudView)')

Write-Host "`n== rule 1 now covers the cloud list too ==" -ForegroundColor Cyan
# Until P8 this test scanned MainViewModel and nothing else, so the cloud list's actions could be -- and
# were -- in no menu at all while the suite reported the menu bar complete. Half an invariant is worse
# than none: it is the half that reports green.
$cloudSrc = Get-Content -Raw (Join-Path $src 'ViewModels\CloudObjectListViewModel.cs')
$cloudNotInMenuBar = @(
    'LoadMoreCommand'         # pane furniture: it pages the list, and means nothing away from it
    'SearchCommand'           # the search box above the list IS the command
    'EnableCheckedCommand'    # the button twins of the three selection commands that ARE in the menu;
    'DisableCheckedCommand'   # they act strictly on the checked set, which is what the count beside
    'RevokeCheckedCommand'    # them and their greying-out promise
    'RefreshCommand'          # View > Refresh already refreshes whichever list is showing
    'ExportAllCsvCommand'     # in File, bound through Cloud. -- see below
    'ExportCsvCommand'        # likewise
)
$cloudCommands = @()
foreach ($m in [regex]::Matches($cloudSrc, '\[RelayCommand[^\]]*\]\s*(?:private|public)\s+(?:async\s+)?(?:Task|void)\s+(\w+?)(Async)?\s*\(')) {
    $cloudCommands += ($m.Groups[1].Value + 'Command')
}
$cloudCommands = @($cloudCommands | Sort-Object -Unique)
Check 'CloudObjectListViewModel has commands' $true ($cloudCommands.Count -ge 10)
$cloudMissing = @()
foreach ($cmd in $cloudCommands) {
    if ($cloudNotInMenuBar -contains $cmd) { continue }
    if (-not ($menuOnly -match [regex]::Escape("{Binding Cloud.$cmd}"))) { $cloudMissing += $cmd }
}
foreach ($m in $cloudMissing) { Write-Host "          $m" -ForegroundColor Yellow }
Check 'every cloud command is in the menu bar' 0 $cloudMissing.Count
# The two exempted exports really are there, just reached through File rather than Action.
foreach ($cmd in 'ExportCsvCommand', 'ExportAllCsvCommand') {
    Check "  File carries Cloud.$cmd" $true ($menuOnly -match [regex]::Escape("{Binding Cloud.$cmd}"))
}

Write-Host "`n== P6 again: the cloud context menu shortens the cloud Action menu ==" -ForegroundColor Cyan
# The cloud list got a context menu in P1 and a menu-bar home in P8, so the same rule now applies to it.
$cloudXaml   = Get-Content -Raw (Join-Path $src 'Views\Controls\CloudObjectListView.xaml')
$cloudCtxXml = Region $cloudXaml '<ContextMenu' '</ContextMenu>'
$cloudLabels = BoundLabels $cloudCtxXml
Check 'the cloud context menu was found' $true ($cloudLabels.Count -ge 4)
$cloudDrift = @()
foreach ($cmd in ($cloudLabels.Keys | Sort-Object)) {
    if (-not $menuLabels.ContainsKey($cmd)) { $cloudDrift += "$cmd is in no menu"; continue }
    $c = $cloudLabels[$cmd][0]
    $ok = $false
    foreach ($m in $menuLabels[$cmd]) { if (IsShorteningOf $c $m) { $ok = $true } }
    if (-not $ok) { $cloudDrift += ($cmd + ": context '" + $c + "' vs menu '" + $menuLabels[$cmd][0] + "'") }
}
foreach ($d in $cloudDrift) { Write-Host "          $d" -ForegroundColor Yellow }
Check 'every cloud context label shortens its menu label' 0 $cloudDrift.Count

Write-Host "`n== menus open rightwards, whatever the machine says ==" -ForegroundColor Cyan
# Reported from a dev build: Action opened right-aligned under its header and Action > New flew out to
# the LEFT. Not the XAML -- Windows has a per-user setting (SM_MENUDROPALIGNMENT) that mirrors menus for
# left-handed pen use, switched on by Tablet PC handedness, and WPF obeys it process-wide. It turns up on
# any machine with a digitizer, usually without the operator choosing it, and there is no public API to
# override it. App.OnStartup overwrites the cached value by reflection instead.
$align = [System.Type]::GetType('UnifiedDirectoryManager.Services.MenuDropAlignment, UnifiedDirectoryManager')
Check 'the helper is present'           $true ($null -ne $align)
$bindStatic = [System.Reflection.BindingFlags]'NonPublic,Static'

# The fragile part is the framework field, not the logic. If .NET renames it, this is the assertion that
# says so -- rather than the override silently doing nothing and menus quietly mirroring again.
$field = [System.Windows.SystemParameters].GetField('_menuDropAlignment', $bindStatic)
Check '  SystemParameters still has the field' $true ($null -ne $field)
Check '  and it is a bool'                     'Boolean' $(if ($field) { $field.FieldType.Name } else { '' })

$hostSetting = [System.Windows.SystemParameters]::MenuDropAlignment
Write-Host "          this machine opens menus $(if ($hostSetting) { 'LEFTwards (the mirrored setting)' } else { 'rightwards (normal)' })" -ForegroundColor DarkGray

# Simulate the mirrored machine, so this proves something on a normal one too rather than passing by
# luck of the host's configuration.
$simulate = $align.GetMethod('SimulateLeftwardMenus', $bindStatic)
$force    = $align.GetMethod('ForceMenusToOpenRightwards', $bindStatic)
Check '  both entry points exist'       $true (($null -ne $simulate) -and ($null -ne $force))
Check '  simulation takes effect'       $true ($simulate.Invoke($null, @()))
Check '  and menus are mirrored'        $false ($align.GetProperty('MenusOpenRightwards', $bindStatic).GetValue($null))
$force.Invoke($null, @()) | Out-Null
Check 'the override turns them back'    $true ($align.GetProperty('MenusOpenRightwards', $bindStatic).GetValue($null))
# Calling it twice must not double-subscribe to the settings-change event, and must still be true after.
$force.Invoke($null, @()) | Out-Null
Check '  and is safe to call twice'     $true ($align.GetProperty('MenusOpenRightwards', $bindStatic).GetValue($null))

# The ORDER inside Apply is the whole trick and is invisible in the code: SystemParameters caches the
# value on first read, so writing the field before anything has read it is undone by that first read.
# Proving it needs a cold cache, which means a fresh process. It only demonstrates anything on a machine
# that is actually configured to mirror menus, so on any other machine this says so rather than passing.
if (-not $hostSetting) {
    Write-Host "          NOT EXERCISED: the ordering proof needs a machine with mirrored menus" -ForegroundColor Yellow
} else {
    $naive = pwsh -NoProfile -STA -Command @'
Add-Type -AssemblyName PresentationFramework
$f = [System.Windows.SystemParameters].GetField('_menuDropAlignment', [System.Reflection.BindingFlags]'NonPublic,Static')
$f.SetValue($null, $false)                       # write FIRST, before any read -- the naive version
[System.Windows.SystemParameters]::MenuDropAlignment
'@
    Check '  writing before reading does nothing' 'True' ("$naive".Trim())
    $correct = pwsh -NoProfile -STA -Command @"
Add-Type -AssemblyName PresentationFramework
[void][System.Reflection.Assembly]::LoadFrom('$dll')
`$t = [System.Type]::GetType('UnifiedDirectoryManager.Services.MenuDropAlignment, UnifiedDirectoryManager')
`$t.GetMethod('ForceMenusToOpenRightwards', [System.Reflection.BindingFlags]'NonPublic,Static').Invoke(`$null, @()) | Out-Null
[System.Windows.SystemParameters]::MenuDropAlignment
"@
    Check '  reading first is what makes it stick' 'False' ("$correct".Trim())
}

# It has to run before any window exists, and after the logger, so a failure is recorded rather than lost.
$appSrc = Get-Content -Raw (Join-Path $src 'App.xaml.cs')
Check 'startup calls it'                $true ($appSrc -match 'MenuDropAlignment\.ForceMenusToOpenRightwards\(\)')
$beforeWindow = $appSrc.IndexOf('ForceMenusToOpenRightwards') -lt $appSrc.IndexOf('new MainWindow')
Check '  before the main window'        $true $beforeWindow
Check '  and after the logger'          $true ($appSrc.IndexOf('AppLog.Instance = logger') -lt $appSrc.IndexOf('ForceMenusToOpenRightwards'))

Write-Host "`n== P9: keyboard shortcuts, and menus that teach them ==" -ForegroundColor Cyan
# Before this the app had no InputBindings outside a few search boxes, so ADUC muscle memory -- F5,
# Delete, Ctrl+F -- failed silently. Nothing announced them either, because there was nothing to announce.
$winBindings = Region $mainXaml '<Window.InputBindings>' '</Window.InputBindings>'
Check 'the window declares gestures'    $true ($winBindings.Length -gt 0)

# Gesture -> the command it must invoke, and the menu item that must advertise it.
$shortcuts = @(
    @{ Gesture = 'F5';         Key = 'F5';  Mod = '';        Command = 'RefreshCommand';        Scope = 'window' }
    @{ Gesture = 'Ctrl+F';     Key = 'F';   Mod = 'Control'; Command = 'AdvancedSearchCommand'; Scope = 'window' }
    @{ Gesture = 'Ctrl+N';     Key = 'N';   Mod = 'Control'; Command = 'NewUserCommand';        Scope = 'window' }
    @{ Gesture = 'F1';         Key = 'F1';  Mod = '';        Command = 'ViewReadmeCommand';     Scope = 'window' }
    @{ Gesture = 'Del';        Key = '';    Mod = '';        Command = 'DeleteSelectedCommand'; Scope = 'list'   }
    @{ Gesture = 'Alt+Enter';  Key = '';    Mod = '';        Command = 'OpenSelectedCommand';   Scope = 'list'   }
)
foreach ($s in $shortcuts) {
    if ($s.Scope -eq 'window') {
        $pattern = if ($s.Mod) { '<KeyBinding Key="' + $s.Key + '" Modifiers="' + $s.Mod + '" Command="\{Binding ' + $s.Command + '\}"' }
                   else        { '<KeyBinding Key="' + $s.Key + '" Command="\{Binding ' + $s.Command + '\}"' }
        Check "  $($s.Gesture) invokes $($s.Command)" $true ($winBindings -match $pattern)
    }
    # A menu that advertises a gesture it does not have is worse than one that advertises none: the
    # operator learns it, it fails, and they stop trusting the others.
    $item = [regex]::Match($menuOnly, '<MenuItem[^/>]*Command="\{Binding (?:Cloud\.)?' + $s.Command + '\}"[^/>]*/>').Value
    Check "  and a menu item announces it"  $true ($item -match ('InputGestureText="' + [regex]::Escape($s.Gesture) + '"'))
}

# Nothing may claim a gesture that is not wired anywhere.
$announced = @()
foreach ($m in [regex]::Matches($menuOnly, 'InputGestureText="([^"]*)"')) { $announced += $m.Groups[1].Value }
$unwired = @($announced | Sort-Object -Unique | Where-Object { $_ -notin $shortcuts.Gesture })
foreach ($u in $unwired) { Write-Host "          the menu promises $u and nothing implements it" -ForegroundColor Yellow }
Check 'no menu promises a dead gesture'  0 $unwired.Count

Write-Host "`n== P9: Delete is scoped to the lists, not the window ==" -ForegroundColor Cyan
# The hazard that decided the design. A focused TextBox always marks Delete handled -- verified, even
# when it is empty and has nothing to delete -- so text editing would have been safe either way. A
# TreeView does not. A window-level Delete would therefore fire while the operator was browsing folders
# in the tree, and silently mean "delete whatever is selected over in the list".
Check 'Delete is not a window gesture'  $false ($winBindings -match 'Key="Delete"')
Check '  nor is Alt+Enter'              $false ($winBindings -match 'Key="Return"')
Check 'the on-prem list handles keys'   $true  ($listXaml -match 'KeyDown="OnListKeyDown"')

$listCb = Get-Content -Raw (Join-Path $src 'Views\Controls\ObjectListView.xaml.cs')
$handler = [regex]::Match($listCb, '(?s)private void OnListKeyDown\(.*?\n    \}').Value
Check '  the handler exists'            $true ($handler.Length -gt 0)
Check '  Delete asks the host'          $true ($handler -match 'Key\.Delete' -and $handler -match 'RequestDelete\(\)')
Check '  and only unmodified'           $true ($handler -match 'Key\.Delete[^;]*ModifierKeys\.None')
Check '  Alt\+Enter opens the row'      $true ($handler -match 'Key\.Enter[^;]*ModifierKeys\.Alt' -and $handler -match 'RequestOpen\(row\)')
# The list must not know how to delete: the host owns the confirmation, and the keyboard has to reach the
# same command the menu does or it gets a different one.
Check '  the list does not delete'      $false ($listCb -match 'DeleteAsync|_directory\.')
$listVmSrc = Get-Content -Raw (Join-Path $src 'ViewModels\ObjectListViewModel.cs')
Check '  it raises an event instead'    $true ($listVmSrc -match 'public event EventHandler\? DeleteRequested')
Check 'the host runs the same command'  $true ($vmSrc2 -match 'List\.DeleteRequested \+= .*DeleteSelectedCommand\.Execute')
Check '  and checks CanExecute first'   $true ($vmSrc2 -match 'List\.DeleteRequested \+= .*DeleteSelectedCommand\.CanExecute')

# The cloud list gets Alt+Enter too, so the two Action menus do not disagree about it. Its commands are
# its own, so there the binding resolves against the list's DataContext directly.
Check 'the cloud list has Alt+Enter'    $true ($cloudXaml -match '(?s)<ListView\.InputBindings>.*Key="Return" Modifiers="Alt" Command="\{Binding OpenSelectedCommand\}"')
Check '  and no cloud Delete'           $false ($cloudXaml -match '<KeyBinding Key="Delete"')

Write-Host "`n== P9: a gesture has nothing to grey out, so the command must refuse ==" -ForegroundColor Cyan
# Ctrl+N and Ctrl+F have no menu item to disable in the cloud view -- the whole AD Action menu is swapped
# away -- so without a gate the shortcut would open an on-prem wizard over a cloud list.
foreach ($pair in @(@('NewUser', 'IsAdView'), @('AdvancedSearch', 'IsAdView'), @('DeleteSelectedAsync', 'HasSelection'))) {
    $decl = [regex]::Match($vmSrc2, '\[RelayCommand\(CanExecute = nameof\((\w+)\)\)\]\s*(?:private|public)[^\n]*\b' + [regex]::Escape($pair[0]) + '\(')
    Check "  $($pair[0]) is gated on $($pair[1])" $pair[1] ($decl.Groups[1].Value)
}
$viewChanged = [regex]::Match($vmSrc2, '(?s)partial void OnIsCloudViewChanged\(bool value\).*?\n    \}').Value
Check 'switching view re-evaluates them' $true (($viewChanged -match 'NewUserCommand\.NotifyCanExecuteChanged') -and ($viewChanged -match 'AdvancedSearchCommand\.NotifyCanExecuteChanged'))
Check 'and selection re-evaluates Delete' $true ($sel -match 'DeleteSelectedCommand\.NotifyCanExecuteChanged')

Write-Host "`n== P9: the two WPF behaviours the design rests on ==" -ForegroundColor Cyan
# Neither of these is documented anywhere obvious, both were measured, and both would silently change the
# meaning of the shortcuts if a future .NET changed them. So they are asserted rather than remembered.
#
# This does NOT construct MainWindow -- that needs the whole service graph, which no suite builds. It
# proves the MECHANISM on this runtime; that the app spells the command names correctly is covered by
# "every bound command is defined" above, which reads the InputBindings like any other binding.
[System.Reflection.Assembly]::LoadFrom((Join-Path $repoRoot 'debug\CommunityToolkit.Mvvm.dll')) | Out-Null

$probeXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Window.InputBindings>
    <KeyBinding Key="F5" Command="{Binding Refresh}" />
    <KeyBinding Key="Delete" Command="{Binding Kill}" />
  </Window.InputBindings>
  <StackPanel>
    <TextBox x:Name="Box" />
    <ListBox x:Name="Plain"><ListBoxItem>one</ListBoxItem></ListBox>
  </StackPanel>
</Window>
'@

$fired = @{ Refresh = 0; Kill = 0 }
$probeVm = [pscustomobject]@{
    Refresh = [CommunityToolkit.Mvvm.Input.RelayCommand]::new([System.Action]{ $fired.Refresh++ })
    Kill    = [CommunityToolkit.Mvvm.Input.RelayCommand]::new([System.Action]{ $fired.Kill++ })
}
$pw = [System.Windows.Markup.XamlReader]::Parse($probeXaml)
$pw.DataContext = $probeVm
$pw.Show()
[System.Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, 'Background') | Out-Null

# 1. A Binding on KeyBinding.Command resolves from DataContext. InputBindings are not in the visual tree,
#    so this is not obvious -- and it is null until the window is shown, which is why nothing reads it
#    earlier.
Check 'KeyBinding.Command resolves'     $true ([object]::ReferenceEquals($pw.InputBindings[0].Command, $probeVm.Refresh))

function ProbeKey([string]$key) {
    $src = [System.Windows.PresentationSource]::FromVisual($pw)
    $e = [System.Windows.Input.KeyEventArgs]::new([System.Windows.Input.Keyboard]::PrimaryDevice, $src, 0, $key)
    $e.RoutedEvent = [System.Windows.Input.Keyboard]::KeyDownEvent
    [System.Windows.Input.InputManager]::Current.ProcessInput($e) | Out-Null
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, 'Background') | Out-Null
}
function ProbeFocus($el) { $el.Focus() | Out-Null; [System.Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, 'Background') | Out-Null }

$pbox = $pw.FindName('Box'); $plist = $pw.FindName('Plain')

# 2. A focused TextBox marks Delete handled even when there is nothing to delete, so a window-level
#    Delete could never interrupt typing. Checked with an EMPTY box, which is the case that would bite.
$pbox.Text = ''; ProbeFocus $pbox
$before = $fired.Kill; ProbeKey 'Delete'
Check '  an empty TextBox still eats Delete' $before $fired.Kill

# 3. A plain ListBox does not. THIS is why Delete is scoped to the object list instead of the window:
#    the tree would have fired it too, meaning "delete what is selected somewhere else".
ProbeFocus $plist
$before = $fired.Kill; ProbeKey 'Delete'
Check '  but a list does not'                ($before + 1) $fired.Kill

# 4. F5 reaches the window binding from inside a TextBox, which is why it IS window-scoped.
ProbeFocus $pbox
$before = $fired.Refresh; ProbeKey 'F5'
Check '  F5 gets through from a TextBox'     ($before + 1) $fired.Refresh
$pw.Close()

Write-Host "`n== T1: the toolbar owns nothing, and now it has to ==" -ForegroundColor Cyan
# Rule 3 used to hold by luck. Once an operator can REMOVE a button it becomes a safety requirement: a
# command reachable only from the toolbar would lose its last route the moment someone took it off, with
# no way back except working out that customisation is where it went.
#
# The catalogue is where that is checked instead of remembered.
$cat = [System.Type]::GetType('UnifiedDirectoryManager.Services.ToolbarCatalogue, UnifiedDirectoryManager')
Check 'the catalogue exists'            $true ($null -ne $cat)
$items = $cat.GetProperty('All').GetValue($null)
$defaults = @($cat.GetProperty('DefaultIds').GetValue($null))
Check '  it offers a useful number'     $true ($items.Count -ge 20)
Check '  and has defaults'              $true ($defaults.Count -ge 8)

$mainVmType = VmType 'MainViewModel'
$offenders = @()
$labelDrift = @()
foreach ($item in $items) {
    # (a) the command has to resolve, or the button is dead on arrival and looks merely disabled
    $type = $mainVmType
    foreach ($segment in ($item.CommandName -split '\.')) {
        if ($null -eq $type) { break }
        $prop = $type.GetProperty($segment)
        $type = if ($null -eq $prop) { $null } else { $prop.PropertyType }
    }
    if ($null -eq $type) { $offenders += "$($item.Id): $($item.CommandName) does not resolve"; continue }

    # (b) rule 3 itself -- it must also be in the menu bar
    $leaf = ($item.CommandName -split '\.')[-1]
    $inMenu = ($menuOnly -match [regex]::Escape("{Binding $($item.CommandName)}")) -or
              ($menuOnly -match [regex]::Escape("{Binding $leaf}"))
    if (-not $inMenu) { $offenders += "$($item.Id): $($item.CommandName) is NOT in the menu bar"; continue }

    # (c) and it must say what the menu says (audit P6), so it can be found by scanning for the word
    $ok = $false
    foreach ($m in $menuLabels[$leaf]) { if ((LabelText $m) -eq (LabelText $item.Label)) { $ok = $true } }
    if (-not $ok) { $labelDrift += "$($item.Id): toolbar '$($item.Label)' vs menu '$($menuLabels[$leaf][0])'" }
}
foreach ($o in $offenders)   { Write-Host "          $o" -ForegroundColor Yellow }
foreach ($d in $labelDrift)  { Write-Host "          $d" -ForegroundColor Yellow }
Check 'every catalogue command is in the menu bar' 0 $offenders.Count
Check 'every catalogue label matches the menu'     0 $labelDrift.Count

# Ids are written into settings.json, so changing one silently resets that operator's toolbar.
$ids = @($items | ForEach-Object { $_.Id })
Check '  ids are unique'                $ids.Count (@($ids | Sort-Object -Unique).Count)
Check '  none collides with separator'  $false ($ids -contains 'separator')
$badDefault = @($defaults | Where-Object { $_ -ne 'separator' -and $_ -notin $ids })
Check '  every default is a real id'    0 $badDefault.Count

Write-Host "`n== T1: a saved layout survives being wrong ==" -ForegroundColor Cyan
# A settings file written by a NEWER build knows ids this one does not. Dropping the layout wholesale
# would be the easy response and the wrong one: the operator loses an arrangement they built, over a
# button that simply is not here yet.
$normalise = $cat.GetMethod('Normalise')
# The argument array has to be built element by element. Writing @([IEnumerable[string]]$ids) looks
# right and is not: @() ENUMERATES the cast collection, so Invoke is handed one argument per id and
# fails with a parameter-count mismatch.
function Invoke1($method, $arg) {
    $box = [object[]]::new(1)
    $box[0] = $arg
    return $method.Invoke($null, $box)
}
function Norm([string[]]$ids) { @(Invoke1 $normalise ([string[]]$ids)) }

Check 'nothing saved means the defaults' ($defaults -join ',') ((Norm @()) -join ',')
Check '  and so does null'               ($defaults -join ',') (@(Invoke1 $normalise $null) -join ',')
Check 'an unknown id is dropped'         'refresh,bulk-edit' ((Norm @('refresh', 'no-such-button', 'bulk-edit')) -join ',')
Check '  and the rest keep their order'  'bulk-edit,refresh' ((Norm @('bulk-edit', 'nope', 'refresh')) -join ',')
Check 'separators repeat'                'refresh,separator,bulk-edit,separator,delete' ((Norm @('refresh','separator','bulk-edit','separator','delete')) -join ',')
Check '  but never lead'                 'refresh' ((Norm @('separator', 'refresh')) -join ',')
Check '  never trail'                    'refresh' ((Norm @('refresh', 'separator')) -join ',')
Check '  and never double'               'refresh,separator,bulk-edit' ((Norm @('refresh','separator','separator','bulk-edit')) -join ',')
# Dropping unknown ids can leave two separators adjacent that were not adjacent when saved.
Check '  including after a drop'         'refresh,separator,bulk-edit' ((Norm @('refresh','separator','gone','separator','bulk-edit')) -join ',')
# A row of nothing but separators is a rendering artefact, not a layout anyone chose.
Check 'separators alone fall back'       ($defaults -join ',') ((Norm @('separator', 'separator')) -join ',')
Check 'all-unknown falls back too'       ($defaults -join ',') ((Norm @('gone', 'also-gone')) -join ',')

Write-Host "`n== T1: the glyphs exist in the font that ships with Windows ==" -ForegroundColor Cyan
# A wrong codepoint renders as an empty box, which reads as a missing font rather than as a typo -- so
# they are checked against the installed Segoe MDL2 Assets rather than trusted from a table.
$fontPath = Join-Path $env:SystemRoot 'Fonts\segmdl2.ttf'
if (-not (Test-Path $fontPath)) {
    Write-Host "          NOT EXERCISED: Segoe MDL2 Assets is not installed here" -ForegroundColor Yellow
} else {
    $gt = [Windows.Media.GlyphTypeface]::new([Uri]$fontPath)
    $missing = @()
    $glyphCount = 0
    foreach ($item in $items) {
        if ([string]::IsNullOrEmpty($item.Glyph)) { continue }
        $glyphCount++
        $cp = [int][char]$item.Glyph[0]
        if (-not $gt.CharacterToGlyphMap.ContainsKey($cp)) { $missing += ("{0}: U+{1:X4}" -f $item.Id, $cp) }
    }
    foreach ($m in $missing) { Write-Host "          $m" -ForegroundColor Yellow }
    Check 'the catalogue uses glyphs'   $true ($glyphCount -ge 10)
    Check 'every glyph is in the font'  0 $missing.Count
    # Prove the lookup can fail, rather than passing because ContainsKey is always true.
    Check '  and a made-up one is not'  $false ($gt.CharacterToGlyphMap.ContainsKey(0xE0FF))
}

# P7 again, from the other side. The toolbar is ALLOWED to duplicate the menu bar -- that is what rule
# 3 says it is for -- so this stops someone "finishing" P7 by taking Refresh and the logs off it. The
# question moved from the XAML to the catalogue when the toolbar stopped being written out by hand.
$dupeOk = @($items | Where-Object { $_.Id -eq 'refresh' -or $_.Id -eq 'logs-folder' })
Check 'the toolbar may still duplicate'  2 $dupeOk.Count
$refreshItem = $items | Where-Object { $_.Id -eq 'refresh' }
Check '  and Refresh works in both views' 'Both' ($refreshItem.Scope.ToString())

Write-Host "`n== T1: text only where an icon would mislead ==" -ForegroundColor Cyan
# Two rules from the design: no icon beats a vague icon, and destructive items keep their words.
$usesTextOnly = $cat.GetMethod('UsesTextOnly')
# Where-Object hands back a PSObject WRAPPER, which reflection cannot convert to the parameter type.
# Unwrap before every Invoke, or the call fails with a conversion error that reads like a bad signature.
function Item([string]$id) { ($items | Where-Object { $_.Id -eq $id }).PSObject.BaseObject }
$deleteItem = Item 'delete'
Check 'Delete is on the toolbar at all' $true ($null -ne $deleteItem)
Check '  and never shows as an icon'    $true (Invoke1 $usesTextOnly $deleteItem)
Check '  while Refresh may'             $false (Invoke1 $usesTextOnly (Item 'refresh'))
$vmRow = [System.Type]::GetType('UnifiedDirectoryManager.ViewModels.ToolbarButtonViewModel, UnifiedDirectoryManager')
$forMethod = $vmRow.GetMethod('For', [System.Reflection.BindingFlags]'Public,Static')
$noop = [CommunityToolkit.Mvvm.Input.RelayCommand]::new([System.Action]{})
$box = [object[]]::new(2); $box[0] = $deleteItem; $box[1] = $noop
$deleteRow = $forMethod.Invoke($null, $box)
Check '  so its rendered glyph is blank' '' $deleteRow.Glyph
Check '  and it reports no glyph'        $false $deleteRow.HasGlyph

Write-Host "`n== T1: the customisation surfaces ==" -ForegroundColor Cyan
# Right-clicking the thing you want to change is where a Windows user looks first, and finding nothing
# there teaches that it cannot be changed.
$toolbarXml2 = Region $mainXaml '<ToolBarTray' '</ToolBarTray>'
Check 'the toolbar is data-driven'      $true ($toolbarXml2 -match 'ItemsSource="\{Binding ToolbarItems\}"')
Check '  with no hand-written buttons'  $false ($toolbarXml2 -match '<Button Content=')
Check '  and its own right-click'       $true ($toolbarXml2 -match 'CustomiseToolbarCommand')
$settingsXaml = Get-Content -Raw (Join-Path $src 'Views\Dialogs\SettingsWindow.xaml')
Check 'Settings has a Toolbar page'     $true ($settingsXaml -match '<TabItem Header="Toolbar"')
foreach ($cmd in 'AddCommand', 'RemoveCommand', 'MoveUpCommand', 'MoveDownCommand', 'ResetToDefaultsCommand', 'SaveCommand') {
    Check "  the page offers $cmd"      $true ($settingsXaml -match [regex]::Escape("{Binding $cmd}"))
}
# Tab headers are matched by their words, which is fragile the moment one is renamed.
$tabsType = [System.Type]::GetType('UnifiedDirectoryManager.Services.SettingsTabs, UnifiedDirectoryManager')
Check 'the tab names are constants'     $true ($null -ne $tabsType)
$declared = @($tabsType.GetProperty('All').GetValue($null))
$actual = @([regex]::Matches($settingsXaml, '<TabItem Header="([^"]*)"') | ForEach-Object { $_.Groups[1].Value })
Check '  and match the dialog exactly'  ($actual -join '|') ($declared -join '|')

Write-Host "`n== T1: the customisation page actually rearranges things ==" -ForegroundColor Cyan
# The assertions above prove the page EXISTS and that its catalogue is safe. These prove it works: the
# four buttons are the whole feature, and a Move that silently does nothing looks exactly like a list
# that was already in that order.
Add-Type -ReferencedAssemblies (Join-Path $repoRoot 'debug\UnifiedDirectoryManager.dll') @'
using UnifiedDirectoryManager.Services;
public sealed class CountingSettingsStore : ISettingsStore {
    public int Saves;
    public AppSettings Last;
    public AppSettings Load() { return new AppSettings(); }
    public void Save(AppSettings settings) { Saves++; Last = settings; }
    public string RecoveredFrom { get { return null; } }
}
'@
if (-not ('CountingSettingsStore' -as [type])) { throw 'the fake store did not compile -- everything below would be a false pass' }

$EditorVm = [UnifiedDirectoryManager.ViewModels.ToolbarEditorViewModel]
function NewEditor([string[]]$saved) {
    $store = [CountingSettingsStore]::new()
    $s = [UnifiedDirectoryManager.Services.AppSettings]::new()
    foreach ($id in $saved) { $s.ToolbarItemIds.Add($id) }
    $vm = $EditorVm::new($store, $s)
    return @{ Vm = $vm; Store = $store; Settings = $s }
}
function ChosenIds($vm) { ($vm.Chosen | ForEach-Object { $_.Id }) -join ',' }

$e = NewEditor @()
Check 'it opens on the saved layout'    (($defaults) -join ',') (ChosenIds $e.Vm)
Check '  and offers the whole catalogue' ($items.Count + 1) $e.Vm.Available.Count   # +1 for the separator
Check '  with a separator to add'        $true (@($e.Vm.Available | Where-Object { $_.Id -eq 'separator' }).Count -eq 1)

# --- Add lands AFTER the selected row, which is how every list like this behaves -------------------
$e = NewEditor @('refresh', 'bulk-edit')
$e.Vm.SelectedAvailable = $e.Vm.Available | Where-Object { $_.Id -eq 'delete' }
$e.Vm.SelectedChosen = $e.Vm.Chosen[0]
Check 'Add is offered'                  $true $e.Vm.AddCommand.CanExecute($null)
$e.Vm.AddCommand.Execute($null)
Check '  and inserts after the selection' 'refresh,delete,bulk-edit' (ChosenIds $e.Vm)
Check '  selecting what it added'       'delete' $e.Vm.SelectedChosen.Id
# The same button twice would be confusing rather than useful.
$e.Vm.SelectedAvailable = $e.Vm.Available | Where-Object { $_.Id -eq 'delete' }
Check '  but not the same one twice'    $false $e.Vm.AddCommand.CanExecute($null)
# ...except a separator, which is the only thing a layout needs more than one of.
$e.Vm.SelectedAvailable = $e.Vm.Available | Where-Object { $_.Id -eq 'separator' }
Check '  while separators repeat'       $true $e.Vm.AddCommand.CanExecute($null)

# --- Remove, and the floor under it ----------------------------------------------------------------
$e = NewEditor @('refresh', 'bulk-edit')
$e.Vm.SelectedChosen = $e.Vm.Chosen[0]
$e.Vm.RemoveCommand.Execute($null)
Check 'Remove takes the row out'        'bulk-edit' (ChosenIds $e.Vm)
# An empty saved list means "use the defaults", so emptying the toolbar would silently restore it. The
# last row therefore cannot be removed -- stated here because it is a surprising rule to meet cold.
Check '  but never the last one'        $false $e.Vm.RemoveCommand.CanExecute($null)

# --- Move -------------------------------------------------------------------------------------------
$e = NewEditor @('refresh', 'bulk-edit', 'delete')
$e.Vm.SelectedChosen = $e.Vm.Chosen[2]
$e.Vm.MoveUpCommand.Execute($null)
Check 'Move up moves it up'             'refresh,delete,bulk-edit' (ChosenIds $e.Vm)
Check '  and the selection follows'     'delete' $e.Vm.SelectedChosen.Id
$e.Vm.MoveDownCommand.Execute($null)
Check 'Move down puts it back'          'refresh,bulk-edit,delete' (ChosenIds $e.Vm)
$e.Vm.SelectedChosen = $e.Vm.Chosen[0]
Check '  the top row cannot go up'      $false $e.Vm.MoveUpCommand.CanExecute($null)
$e.Vm.SelectedChosen = $e.Vm.Chosen[2]
Check '  nor the bottom one down'       $false $e.Vm.MoveDownCommand.CanExecute($null)

# --- Save, Reset, and the round trip ----------------------------------------------------------------
$e = NewEditor @('refresh', 'bulk-edit')
$e.Vm.SelectedChosen = $e.Vm.Chosen[1]
$e.Vm.SelectedAvailable = $e.Vm.Available | Where-Object { $_.Id -eq 'separator' }
$e.Vm.AddCommand.Execute($null)
Check 'a trailing separator is allowed while editing' 'refresh,bulk-edit,separator' (ChosenIds $e.Vm)
$e.Vm.SaveCommand.Execute($null)
Check 'Save writes to the store'        1 $e.Store.Saves
# Saving stores what will actually be RENDERED, so reopening the page shows the truth rather than a
# trailing separator that the toolbar was always going to drop.
Check '  and tidies on the way out'     'refresh,bulk-edit' (($e.Settings.ToolbarItemIds) -join ',')
Check '  the page agrees afterwards'    'refresh,bulk-edit' (ChosenIds $e.Vm)

$e = NewEditor @('delete')
$e.Vm.ResetToDefaultsCommand.Execute($null)
Check 'Reset restores the defaults'     (($defaults) -join ',') (ChosenIds $e.Vm)
Check '  without saving by itself'      0 $e.Store.Saves
$e.Vm.SaveCommand.Execute($null)
Check '  until Save is pressed'         1 $e.Store.Saves

# Nothing the editor can produce may be rejected by the renderer.
$e = NewEditor @('refresh')
$e.Vm.SaveCommand.Execute($null)
Check 'a one-button toolbar survives'   'refresh' (($e.Settings.ToolbarItemIds) -join ',')

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
