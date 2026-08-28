<#
.SYNOPSIS
  Loads the Advanced Search dialog off-screen and checks the saved-search Pin button actually renders.

.DESCRIPTION
  The button's label and visibility are bound, and a broken binding in WPF is SILENT: the button would
  simply show nothing, or show while there is nowhere to pin to. Neither throws, so only rendering the
  window and looking at the result catches it.

  Run with:  pwsh -NoProfile -STA -File ./app/build/test-search-dialog.ps1
#>
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'
[System.Reflection.Assembly]::LoadFrom((Join-Path (Get-Location) 'debug\UnifiedDirectoryManager.dll')) | Out-Null
$app = New-Object UnifiedDirectoryManager.App
$app.GetType().GetMethod('InitializeComponent').Invoke($app, @())
$app.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown

function D($d) {
    $o = @(); $n = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($d)
    for ($i = 0; $i -lt $n; $i++) { $k = [System.Windows.Media.VisualTreeHelper]::GetChild($d, $i); $o += $k; $o += D $k }
    $o
}

$pass = 0; $fail = 0
function Check([string]$n, $e, $a) {
    if ($e -eq $a) { $script:pass++; Write-Host "  PASS  $n" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL  $n (expected $e, got $a)" -ForegroundColor Red }
}

$F = [UnifiedDirectoryManager.Services.Favorites]
$Kind = [UnifiedDirectoryManager.Models.FavoriteKind]
function Search([string]$value) {
    $e = New-Object UnifiedDirectoryManager.Models.FavoriteEntry
    $e.Kind = $Kind::SavedSearch; $e.Value = $value; return $e
}

$dir = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-srchdlg-" + [System.Guid]::NewGuid().ToString('N'))
$store = New-Object UnifiedDirectoryManager.Services.SavedSearchStore $dir
$sv = New-Object UnifiedDirectoryManager.Models.SavedSearch
$sv.Name = 'Disabled users'
$store.Save($sv, $null)

# The same two hooks MainViewModel hands over, against a throwaway settings object.
$settings = New-Object UnifiedDirectoryManager.Services.AppSettings
$isPinned = [System.Func[string, bool]] { param($name) $F::Contains($settings, 'contoso.net', (Search $name)) }
$togglePin = [System.Action[string]] {
    param($name)
    if ($F::Contains($settings, 'contoso.net', (Search $name))) { [void]$F::Remove($settings, 'contoso.net', (Search $name)) }
    else { [void]$F::Add($settings, 'contoso.net', (Search $name)) }
}
$pinning = New-Object UnifiedDirectoryManager.Services.SavedSearchPinning $isPinned, $togglePin

function Open($pin) {
    $Vm = [UnifiedDirectoryManager.ViewModels.AdvancedSearchViewModel]
    # IDialogService is only reached by the save/delete confirmations, which this does not exercise.
    $vm = $Vm::new([UnifiedDirectoryManager.Services.IDialogService]$null, $store, $pin)
    $w = New-Object UnifiedDirectoryManager.Views.Dialogs.AdvancedSearchWindow
    $w.DataContext = $vm
    $w.WindowStyle = 'None'; $w.ShowInTaskbar = $false; $w.Left = -10000; $w.Top = -10000
    $w.Show(); $w.UpdateLayout()
    return @{ Vm = $vm; Window = $w }
}
function Pump {
    $frame = New-Object System.Windows.Threading.DispatcherFrame
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke(
        [System.Windows.Threading.DispatcherPriority]::ApplicationIdle,
        [System.Action] { $frame.Continue = $false }) | Out-Null
    [System.Windows.Threading.Dispatcher]::PushFrame($frame)
}
function Click($btn) {
    $peer = New-Object System.Windows.Automation.Peers.ButtonAutomationPeer $btn
    $peer.GetPattern([System.Windows.Automation.Peers.PatternInterface]::Invoke).Invoke()
    Pump
}
function PinButton($w) {
    @(D $w | Where-Object {
        $_ -is [System.Windows.Controls.Button] -and
        ([string]$_.Content -eq 'Pin' -or [string]$_.Content -eq 'Unpin')
    })[0]
}

Write-Host "`n== the Pin button renders when there is somewhere to pin to ==" -ForegroundColor Cyan
$o = Open $pinning
$btn = PinButton $o.Window
Check 'the button is in the dialog'  $true  ($null -ne $btn)
Check 'and is visible'              'Visible' ([string]$btn.Visibility)
# A blank button is what a broken Content binding looks like; it does not throw.
Check 'and is labelled Pin'         'Pin'  ([string]$btn.Content)
# Nothing is selected in the combo yet, so there is no search to pin. An enabled button that does nothing
# when pressed reads as broken.
Check 'but is off with no selection' $false $btn.IsEnabled

$o.Vm.SelectedSavedSearch = $o.Vm.SavedSearches[0]
$o.Window.UpdateLayout()
Check 'picking a search enables it'  $true  $btn.IsEnabled

# Click it the way an operator would, so the command binding is exercised, not just the view model.
Click $btn
$o.Window.UpdateLayout()
Check 'clicking it pins the search' $true  $F::Contains($settings, 'contoso.net', (Search 'Disabled users'))
Check 'and the label follows'       'Unpin' ([string](PinButton $o.Window).Content)

Click (PinButton $o.Window)
$o.Window.UpdateLayout()
Check 'clicking again unpins'       $false $F::Contains($settings, 'contoso.net', (Search 'Disabled users'))
Check 'and the label reverts'       'Pin'  ([string](PinButton $o.Window).Content)
$o.Window.Close()

Write-Host "`n== and stays out of the way when there is not ==" -ForegroundColor Cyan
# Before a connection there is no domain to file a favourite under, so offering the button would only
# produce a button that does nothing.
$o = Open $null
$btn = PinButton $o.Window
Check 'the button is collapsed' 'Collapsed' ([string]$btn.Visibility)
# And is off as well as hidden, so nothing can reach it by keyboard either.
$o.Vm.SelectedSavedSearch = $o.Vm.SavedSearches[0]
$o.Window.UpdateLayout()
Check 'and stays disabled'      $false      $btn.IsEnabled
$o.Window.Close()

Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
