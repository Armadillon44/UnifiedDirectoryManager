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
$toolbarLabels = BoundLabels $toolbarXml
$listCtxXml    = Region $listXaml '<ContextMenu' '</ContextMenu>'
$listLabels    = BoundLabels $listCtxXml

Check 'the toolbar was found'           $true ($toolbarLabels.Count -ge 8)
Check 'the list context menu was found' $true ($listLabels.Count -ge 8)

# --- the toolbar is not allowed its own vocabulary --------------------------------------------------
# It is a shortcut to a menu item, so it says what that menu item says. Where a label is too long for a
# button, the fix is to shorten the canonical label, not to invent a second one (audit P6).
$toolbarDrift = @()
foreach ($cmd in ($toolbarLabels.Keys | Sort-Object)) {
    if (-not $menuLabels.ContainsKey($cmd)) { continue }
    $t = $toolbarLabels[$cmd][0]
    if ($menuLabels[$cmd] -notcontains $t) {
        $toolbarDrift += ($cmd + ": toolbar '" + $t + "' vs menu '" + $menuLabels[$cmd][0] + "'")
    }
}
foreach ($d in $toolbarDrift) { Write-Host "          $d" -ForegroundColor Yellow }
Check 'every toolbar label matches its menu label' 0 $toolbarDrift.Count

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
$treeCtx = Region $mainXaml '<ContextMenu' '</ContextMenu>'
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
# regression of P7, and saying so here stops someone "fixing" it later.
Check '  the toolbar may still offer Refresh' $true ($toolbarXml -match 'RefreshCommand')

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

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
