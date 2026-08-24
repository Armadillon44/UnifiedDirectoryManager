# Reproduces the pane's tab strip off-screen: a TabControl whose ItemsSource is a CompositeCollection with a
# CollectionContainer first (the sections) and fixed TabItems after it. Confirms the reported bug — the
# control selects a COLLAPSED fixed tab and refilling the sections never moves it, so the pane renders blank —
# and that the bound SelectedIndex the rebuild sets is what puts it right.
#
# Run with:  pwsh -NoProfile -STA -File tabharness.ps1
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

Add-Type @'
using System.ComponentModel;
public class Vm : INotifyPropertyChanged {
    int _i = -1;
    public int SelectedSectionIndex {
        get { return _i; }
        set { _i = value; var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs("SelectedSectionIndex")); }
    }
    public event PropertyChangedEventHandler PropertyChanged;
}
public class Section { public string Title { get; set; } }
'@ -ReferencedAssemblies System.ObjectModel

$pass = 0; $fail = 0
function Check([string]$name, $expected, $actual) {
    if ($expected -eq $actual) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL  $name (expected $expected, got $actual)" -ForegroundColor Red }
}

$vm = New-Object Vm
$sections = New-Object 'System.Collections.ObjectModel.ObservableCollection[Section]'

$tabs = New-Object System.Windows.Controls.TabControl
$composite = New-Object System.Windows.Data.CompositeCollection
$container = New-Object System.Windows.Data.CollectionContainer
$container.Collection = $sections
$composite.Add($container) | Out-Null
# The fixed tabs that follow the sections in the real pane. Collapsed for a distribution group, exactly as
# IsUser / CanManageMemberships / IsGroup leave them.
foreach ($h in 'Licenses', 'Member Of') {
    $ti = New-Object System.Windows.Controls.TabItem
    $ti.Header = $h
    $ti.Visibility = [System.Windows.Visibility]::Collapsed
    $composite.Add($ti) | Out-Null
}
$tabs.ItemsSource = $composite

$win = New-Object System.Windows.Window
$win.Content = $tabs
$win.Width = 400; $win.Height = 300
$win.WindowStyle = [System.Windows.WindowStyle]::None
$win.ShowInTaskbar = $false
$win.Left = -10000; $win.Top = -10000
$win.Show()
function Settle { $win.UpdateLayout(); [System.Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([action]{}, 'Background') }

Write-Host "`n== the bug, with no SelectedIndex binding ==" -ForegroundColor Cyan
1..3 | ForEach-Object { $s = New-Object Section; $s.Title = "Section $_"; $sections.Add($s) }
Settle
# NOT a section: the control picks the first real TabItem it can find, and at first measure the only items
# are the fixed ones. It lands on Licenses and stays there as the sections push its index along.
Check 'the first fill selects the first FIXED tab' 3 $tabs.SelectedIndex
Check 'which is collapsed, so the pane renders blank' ([System.Windows.Visibility]::Collapsed) $tabs.SelectedItem.Visibility

# What the pane does on every reload: tear the sections down and put new ones back.
$sections.Clear(); Settle
Check 'clearing leaves it on that same fixed tab' 0 $tabs.SelectedIndex
1..7 | ForEach-Object { $s = New-Object Section; $s.Title = "Rebuilt $_"; $sections.Add($s) }
Settle
Check 'refilling never moves it to a section (the reported bug)' 7 $tabs.SelectedIndex
Check 'and it is still the collapsed fixed tab' 'Licenses' $tabs.SelectedItem.Header

Write-Host "`n== the fix: a bound SelectedIndex the rebuild sets ==" -ForegroundColor Cyan
$b = New-Object System.Windows.Data.Binding 'SelectedSectionIndex'
$b.Source = $vm
$b.Mode = [System.Windows.Data.BindingMode]::TwoWay
[void][System.Windows.Data.BindingOperations]::SetBinding(
    $tabs, [System.Windows.Controls.TabControl]::SelectedIndexProperty, $b)
Settle

# SelectSection(0): forced through -1 so an unchanged value still raises a notification.
$vm.SelectedSectionIndex = -1
$vm.SelectedSectionIndex = 0
Settle
Check 'the rebuild can select the first tab' 0 $tabs.SelectedIndex
Check 'and it is a section, not a collapsed fixed tab' 'Rebuilt 1' $tabs.SelectedItem.Title

# And again through a full teardown, which is what selecting another row does.
$sections.Clear()
1..4 | ForEach-Object { $s = New-Object Section; $s.Title = "Round two $_"; $sections.Add($s) }
Settle
$vm.SelectedSectionIndex = -1
$vm.SelectedSectionIndex = 0
Settle
Check 'it still works after a second rebuild' 0 $tabs.SelectedIndex
Check 'showing the new first section'        'Round two 1' $tabs.SelectedItem.Title

# The usage read appends rather than rebuilding, and wants the tab it just added.
$s = New-Object Section; $s.Title = 'Size and usage'; $sections.Add($s)
Settle
$vm.SelectedSectionIndex = -1
$vm.SelectedSectionIndex = $sections.Count - 1
Settle
Check 'an appended section can be selected' 'Size and usage' $tabs.SelectedItem.Title

$win.Close()
Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
