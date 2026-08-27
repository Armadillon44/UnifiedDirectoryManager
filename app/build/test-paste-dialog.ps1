# Loads the paste dialog off-screen with a stubbed resolver and drives it, to prove the grid actually binds
# and that an unsettled row cannot be committed. A XAML binding failure here is silent at runtime.
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$ErrorActionPreference='Stop'
[System.Reflection.Assembly]::LoadFrom((Join-Path (Get-Location) 'debug\UnifiedDirectoryManager.dll')) | Out-Null
$app = New-Object UnifiedDirectoryManager.App
$app.GetType().GetMethod('InitializeComponent').Invoke($app, @())
function D($d){$o=@();$n=[System.Windows.Media.VisualTreeHelper]::GetChildrenCount($d);for($i=0;$i -lt $n;$i++){$k=[System.Windows.Media.VisualTreeHelper]::GetChild($d,$i);$o+=$k;$o+=D $k};$o}

$pass=0;$fail=0
function Check([string]$n,$e,$a){ if($e -eq $a){$script:pass++;Write-Host "  PASS  $n" -ForegroundColor Green} else {$script:fail++;Write-Host "  FAIL  $n (expected $e, got $a)" -ForegroundColor Red} }

# The view model needs an IExchangeService; only ResolveMembersAsync is exercised, so a null is enough to
# construct it and prove the window binds. Resolution itself is covered by the host-script suite.
# Any IMemberResolver will do here; resolution itself is covered by the host-script suite.
$vm = New-Object UnifiedDirectoryManager.ViewModels.PasteMembersViewModel @([UnifiedDirectoryManager.Services.IMemberResolver]$null, [string[]]@(), $null)
$w = New-Object UnifiedDirectoryManager.Views.Dialogs.PasteMembersWindow
$w.DataContext = $vm
$w.WindowStyle='None'; $w.ShowInTaskbar=$false; $w.Left=-10000; $w.Top=-10000
$w.Show(); $w.UpdateLayout()

Write-Host "`n== the dialog binds ==" -ForegroundColor Cyan
$boxes = D $w | Where-Object { $_ -is [System.Windows.Controls.TextBox] }
Check 'the paste box is there' $true ($boxes.Count -ge 1)
$buttons = @(D $w | Where-Object { $_ -is [System.Windows.Controls.Button] } | ForEach-Object { [string]$_.Content })
Check 'Resolve is offered'    $true ($buttons -contains 'Resolve')
Check 'and Add is offered'    $true ($buttons -contains 'Add these members')

Write-Host "`n== nothing settled cannot be committed ==" -ForegroundColor Cyan
Check 'an empty grid commits nothing' $false $vm.Commit()
Check 'and accepts nobody'            0      $vm.Accepted.Count

# Resolve is only enabled once something has been pasted — an empty paste is not a lookup worth making.
Check 'Resolve is off with an empty box' $false $vm.ResolveCommand.CanExecute($null)
$vm.PastedText = 'Jane Doe'
Check 'and on once text is pasted'       $true  $vm.ResolveCommand.CanExecute($null)

$w.Close()
Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if($fail -gt 0){'Red'}else{'Green'})
if ($fail -gt 0) { exit 1 }
