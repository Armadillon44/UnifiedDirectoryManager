<#
.SYNOPSIS
  Behavioural tests for renaming groups, and for the GUID-backed group references in templates and scenarios.

.DESCRIPTION
  A rename touches four different services depending on what the group is: an on-prem AD group (synced or not),
  an Entra security group, a Microsoft 365 group or Team, or an Exchange distribution list. This suite drives the
  real GroupRenameService and the dialog's view model against in-memory stand-ins for all of them, and asserts
  on what is WRITTEN: which service was called, with which values, in which order, and what the operator is shown
  afterwards. A suite that only looked at the code would not notice a rename sent to the wrong service.

  The template and scenario stores are the real ones, pointed at a temporary folder that is deleted afterwards.
  Nothing here touches the operator's own templates, scenarios or settings.

  STA because the view model and the rendered dialog need a dispatcher.

  Run with:  pwsh -NoProfile -STA -File ./app/build/test-group-rename.ps1
#>
[CmdletBinding()]
param()

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$debug = Join-Path (Split-Path -Parent $root) 'debug'
foreach ($a in 'UnifiedDirectoryManager.dll', 'UnifiedDirectoryManager.TestSupport.dll') {
    $path = Join-Path $debug $a
    if (-not (Test-Path $path)) { throw "Build first — could not find $path" }
    [System.Reflection.Assembly]::LoadFrom($path) | Out-Null
}

# Continuations must come back to this thread, as they do in the app; see test-busy-indicator.ps1.
[System.Threading.SynchronizationContext]::SetSynchronizationContext(
    [System.Windows.Threading.DispatcherSynchronizationContext]::new(
        [System.Windows.Threading.Dispatcher]::CurrentDispatcher))

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
# PowerShell's -eq ignores case, so Check cannot tell "SMTP:" from "smtp:", which is the whole difference between
# a primary and a secondary address. Where case is the point, this compares exactly.
function CheckExact([string]$name, [string]$expected, [string]$actual) {
    if ($expected -ceq $actual) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else {
        $script:fail++
        Write-Host "  FAIL  $name" -ForegroundColor Red
        Write-Host "          expected: [$expected]"
        Write-Host "          actual:   [$actual]"
    }
}
function Pump {
    $frame = New-Object System.Windows.Threading.DispatcherFrame
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke(
        [System.Windows.Threading.DispatcherPriority]::ApplicationIdle,
        [System.Action] { $frame.Continue = $false }) | Out-Null
    [System.Windows.Threading.Dispatcher]::PushFrame($frame)
}
# A broken build must FAIL a named check, not crash before the tally. The parameter is NOT called $s: PowerShell
# scopes dynamically, so a block reading the suite's own $s would see the parameter instead.
function Safe([scriptblock]$block) { try { & $block } catch { "<error: $($_.Exception.Message)>" } }
# Never blocks on an unfinished task: its remainder needs this thread's dispatcher.
function Run($task) {
    $n = 0; while (-not $task.IsCompleted -and $n -lt 200) { Pump; $n++ }
    if (-not $task.IsCompleted) { return '<still running>' }
    try { return $task.GetAwaiter().GetResult() } catch { return "<threw: $($_.Exception.InnerException.Message ?? $_.Exception.Message)>" }
}
function List([string[]]$items) { $l = [System.Collections.Generic.List[string]]::new(); foreach ($i in $items) { $l.Add($i) }; return ,$l }
function Map() { return [System.Collections.Generic.Dictionary[string,string]]::new() }   # case-SENSITIVE, as JSON gives back
function Multi([string]$ldap, [string[]]$values) {
    $a = [UnifiedDirectoryManager.TestSupport.FakeDirectoryService]::Attr($ldap, $values[0])
    for ($i = 1; $i -lt $values.Count; $i++) { $a.RawValues.Add($values[$i]); $a.DisplayValues.Add($values[$i]) }
    return $a
}
$None = [System.Threading.CancellationToken]::None
$Fake = [UnifiedDirectoryManager.TestSupport.FakeDirectoryService]
$Links = [UnifiedDirectoryManager.Services.GroupLinks]
$Naming = [UnifiedDirectoryManager.Services.GroupNaming]
$Origin = [UnifiedDirectoryManager.Models.GroupRenameOrigin]
function Source($origin, [string]$id) { [UnifiedDirectoryManager.Models.GroupRenameSource]::new($origin, $id, $null) }
function Request([string]$name, [string]$display, $sam, $address) {
    [UnifiedDirectoryManager.Models.GroupRenameRequest]::new($name, $display, $sam, $address)
}

$DOM   = 'DC=contoso,DC=net'
$GRP   = "CN=Sales Team,OU=Groups,$DOM"
$GUID  = '11111111-2222-3333-4444-555555555555'
$SID   = 'S-1-5-21-100-200-300-1101'
$OTHER = "CN=Marketing,OU=Groups,$DOM"
$OGUID = '99999999-8888-7777-6666-555555555555'

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-rename-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$storeN = 0
function Stores() {
    $script:storeN++
    $t = [UnifiedDirectoryManager.Services.TemplateStore]::new((Join-Path $tmp "templates$storeN"))
    $s = [UnifiedDirectoryManager.Services.ScenarioStore]::new((Join-Path $tmp "scenarios$storeN"))
    return @{ T = $t; S = $s }
}

# Sales Team: an on-prem, mail-enabled global security group whose names all follow one another.
function OnPremFake() {
    $f = New-Object UnifiedDirectoryManager.TestSupport.FakeDirectoryService
    $f.Objects[$GRP] = [UnifiedDirectoryManager.Models.AdAttribute[]]@(
        $Fake::Attr('cn', 'Sales Team'), $Fake::Attr('sAMAccountName', 'Sales Team'), $Fake::Attr('displayName', 'Sales Team'),
        $Fake::Attr('groupType', '-2147483646'), $Fake::Attr('objectGUID', $GUID), $Fake::Attr('objectSid', $SID),
        (Multi 'objectClass' @('top', 'group')), $Fake::Attr('mail', 'salesteam@contoso.com'),
        (Multi 'proxyAddresses' @('SMTP:salesteam@contoso.com', 'smtp:sales@contoso.com', 'X500:/o=Old/cn=Sales')))
    $f.ObjectGuids[$GRP] = $GUID
    $f.Sids[$SID] = $GRP
    $f.ObjectGuids[$OTHER] = $OGUID
    return $f
}

try {

Write-Host "`n--- names and addresses ---"
Check 'a group keeps its case, spaces and hyphens'       'IT-Admins Team' ($Naming::DeriveGroupSam('IT-Admins Team'))
Check '  and drops what AD forbids'                       'SalesEast'      ($Naming::DeriveGroupSam('Sales/East*'))
Check '  and never ends with a period'                    'Sales Inc'      ($Naming::DeriveGroupSam('Sales Inc.'))
Check 'a forbidden character is refused'                  $true ([string]$Naming::SamProblem('Sales;East') -like '*;*')
Check 'a trailing period is refused'                      $true ($null -ne $Naming::SamProblem('Sales.'))
Check '256 characters is the group limit'                 $null ($Naming::SamProblem('x' * 256))
Check '  257 is not'                                      $true ($null -ne $Naming::SamProblem('x' * 257))
Check 'an address with a prefix is refused'               $true ([string]$Naming::AddressProblem('SMTP:a@b.com') -like '*prefix*')
Check 'an address with no domain is refused'              $true ($null -ne $Naming::AddressProblem('sales@contoso'))
Check 'a plain address is fine'                           $null ($Naming::AddressProblem('sales@contoso.com'))
Check 'spaces-removed addresses follow the name'          'marketingteam@contoso.com' ($Naming::SuggestAddress('Sales Team', 'salesteam@contoso.com', 'Marketing Team'))
Check '  dotted ones too'                                 'marketing.team@contoso.com' ($Naming::SuggestAddress('Sales Team', 'sales.team@contoso.com', 'Marketing Team'))
Check '  hyphenated ones too'                             'marketing-team@contoso.com' ($Naming::SuggestAddress('Sales Team', 'sales-team@contoso.com', 'Marketing Team'))
CheckExact '  and a capitalised one keeps its capitals'   'MarketingTeam@contoso.com' ($Naming::SuggestAddress('Sales Team', 'SalesTeam@contoso.com', 'Marketing Team'))
CheckExact '  and a lower-case one stays lower case'      'marketingteam@contoso.com' ($Naming::SuggestAddress('Sales Team', 'salesteam@contoso.com', 'Marketing Team'))
Check 'an address not made from the name gets no guess'   $null ($Naming::SuggestAddress('Sales Team', 'sales@contoso.com', 'Marketing Team'))
$plan = $Naming::PlanProxyAddresses([string[]]@('SMTP:old@contoso.com', 'smtp:alias@contoso.com', 'X500:/o=x', 'smtp:new@contoso.com'), 'new@contoso.com')
CheckExact 'the new address becomes the primary, first'   'SMTP:new@contoso.com' $plan[0]
Check '  the old primary stays, as a secondary'           $true ($plan -ccontains 'smtp:old@contoso.com')
Check '  and is no longer a primary'                      $false ($plan -ccontains 'SMTP:old@contoso.com')
Check '  other entries are left alone'                    $true (($plan -ccontains 'smtp:alias@contoso.com') -and ($plan -ccontains 'X500:/o=x'))
Check '  and the new address is not listed twice'         1 (@($plan | Where-Object { $_ -like '*new@contoso.com' })).Count
$DS = [UnifiedDirectoryManager.Services.DirectoryService]
Check 'a renamed DN keeps its parent'                     "CN=Sales East,OU=Groups,$DOM" ($DS::BuildRenamedDn($GRP, 'Sales East'))
Check '  escapes a comma for raw LDAP'                    "CN=Sales\, East,OU=Groups,$DOM" ($DS::BuildRenamedDn($GRP, 'Sales, East'))
Check '  but not a slash (that is an ADSI-path escape)'   "CN=Sales/East,OU=Groups,$DOM" ($DS::BuildRenamedDn($GRP, 'Sales/East'))
Check '  and trims the name'                              "CN=Sales East,OU=Groups,$DOM" ($DS::BuildRenamedDn($GRP, '  Sales East '))

Write-Host "`n--- stored references follow the group's GUID ---"
$f = OnPremFake
$dns = List @($GRP); $guids = Map
$r = Run ($Links::ResolveAsync($f, $dns, $guids, $None))
Check 'a reference without a GUID learns it'              $GUID $guids[$GRP]
Check '  and reports the change, so it gets saved'        $true $r.Changed
$moved = "CN=Sales Team,OU=Moved,$DOM"
$f.ObjectGuids.Remove($GRP) | Out-Null; $f.ObjectGuids[$moved] = $GUID
$r = Run ($Links::ResolveAsync($f, $dns, $guids, $None))
Check 'a moved group is found by its GUID'                $moved $dns[0]
Check '  and acted on where it is now'                    $moved ($r.CurrentDns -join ';')
Check '  and its GUID is filed under the new DN'          $GUID ($Links::GuidFor($guids, $moved))
# The old DN now belongs to a different group: it must not be mistaken for the deleted one.
$f.ObjectGuids.Remove($moved) | Out-Null; $f.ObjectGuids[$GRP] = 'aaaaaaaa-0000-0000-0000-000000000000'
$dns2 = List @($moved); $g2 = Map; $g2[$moved] = $GUID
$r = Run ($Links::ResolveAsync($f, $dns2, $g2, $None))
Check 'a deleted group is reported missing'               1 $r.Missing.Count
Check '  and nothing is acted on in its place'            0 $r.CurrentDns.Count
Check '  even when its old name is taken by a new group'  $moved $dns2[0]
$cased = Map; $cased[$GRP.ToUpperInvariant()] = $GUID   # a map read back from JSON matches keys by exact case
Check 'a GUID is found whatever the case of the DN'       $GUID ($Links::GuidFor($cased, $GRP))
$keep = $Links::Carry($cased, [string[]]@($GRP))
Check 'an editor save keeps the GUIDs still listed'       $GUID ($Links::GuidFor($keep, $GRP))
$drop = $Links::Carry($cased, [string[]]@($OTHER))
Check '  and drops the ones it no longer lists'           0 $drop.Count
$rd = List @($GRP, $OTHER); $rg = Map; $rg[$OTHER] = $OGUID
Check 'retargeting moves a reference named by the old DN' $true ($Links::Retarget($rd, $rg, $GUID, $GRP, "CN=Sales East,OU=Groups,$DOM"))
Check '  to the new DN'                                   "CN=Sales East,OU=Groups,$DOM" $rd[0]
Check '  recording the GUID'                              $GUID ($Links::GuidFor($rg, $rd[0]))
Check '  and leaves other groups alone'                   $OTHER $rd[1]
$rd = List @($GRP); $rg = Map; $rg[$GRP] = $OGUID
Check 'a different group at the old DN is not retargeted' $false ($Links::Retarget($rd, $rg, $GUID, $GRP, "CN=X,$DOM"))

Write-Host "`n--- templates and scenarios on disk ---"
$st = Stores
$tpl = New-Object UnifiedDirectoryManager.Models.UserTemplate
$tpl.Name = 'Sales hire'; $tpl.GroupDns.Add($GRP); $tpl.GroupDns.Add($OTHER)
$ref = New-Object UnifiedDirectoryManager.Models.CloudGroupRef; $ref.Id = 'm365-id'; $ref.Name = 'Old cloud name'; $tpl.CloudGroups.Add($ref)
$dl = New-Object UnifiedDirectoryManager.Models.DistributionGroupRef; $dl.Id = ''; $dl.Name = 'All Sales'; $dl.Smtp = 'allsales@contoso.com'; $tpl.DistributionGroups.Add($dl)
$st.T.Save($tpl, $null)
$sc = New-Object UnifiedDirectoryManager.Models.Scenario; $sc.Name = 'Leaver'
$step = New-Object UnifiedDirectoryManager.Models.ScenarioStep
$step.Action = [UnifiedDirectoryManager.Models.ScenarioActionType]::RemoveFromGroups; $step.GroupDns.Add($GRP); $step.GroupGuids[$GRP] = $GUID
$fwd = New-Object UnifiedDirectoryManager.Models.ForwardingTargetRef; $fwd.Identity = 'allsales@contoso.com'; $fwd.Name = 'All Sales'; $step.ForwardingTarget = $fwd
$sc.Steps.Add($step); $st.S.Save($sc, $null)
$f = OnPremFake
$maint = [UnifiedDirectoryManager.Services.GroupReferenceMaintenance]::new($f, $st.T, $st.S)
$sweep = Run ($maint.UpgradeAllAsync($None))
$back = $st.T.LoadAll()[0]
Check 'the sweep gives an old template its GUIDs'         $GUID ($Links::GuidFor($back.GroupGuids, $GRP))
Check '  for every group it lists'                        $OGUID ($Links::GuidFor($back.GroupGuids, $OTHER))
Check '  and saves only what changed'                     1 $sweep.Saved
$f.ObjectGuids.Remove($GRP) | Out-Null; $f.ObjectGuids["CN=Sales Team,OU=Moved,$DOM"] = $GUID
$sweep = Run ($maint.UpgradeAllAsync($None))
Check 'a group moved in ADUC is followed in the template' "CN=Sales Team,OU=Moved,$DOM" ($st.T.LoadAll()[0].GroupDns[0])
Check '  and in the scenario'                             "CN=Sales Team,OU=Moved,$DOM" ($st.S.LoadAll()[0].Steps[0].GroupDns[0])
Check 'retargeting after an in-app rename'                2 (Run ($maint.RetargetGroupAsync($GUID, "CN=Sales Team,OU=Moved,$DOM", "CN=Sales East,OU=Moved,$DOM")))
Check '  reaches the template'                            "CN=Sales East,OU=Moved,$DOM" ($st.T.LoadAll()[0].GroupDns[0])
Check '  and the scenario'                                "CN=Sales East,OU=Moved,$DOM" ($st.S.LoadAll()[0].Steps[0].GroupDns[0])
Check 'a cloud rename refreshes the files that use it'    1 (Run ($maint.RefreshCloudGroupAsync('m365-id', 'New cloud name', $null, $null)))
Check '  the template''s cloud group'                     'New cloud name' ($st.T.LoadAll()[0].CloudGroups[0].Name)
Check 'a list with no ID is matched by its old address'   2 (Run ($maint.RefreshCloudGroupAsync('dl-id', 'All Sales East', 'allsales@contoso.com', 'allsaleseast@contoso.com')))
Check '  its name'                                        'All Sales East' ($st.T.LoadAll()[0].DistributionGroups[0].Name)
Check '  and the address members are added through'      'allsaleseast@contoso.com' ($st.T.LoadAll()[0].DistributionGroups[0].Smtp)
Check '  and a scenario forwarding to it'                 'allsaleseast@contoso.com' ($st.S.LoadAll()[0].Steps[0].ForwardingTarget.Identity)

Write-Host "`n--- a rename during a sweep does not freeze the window ---"
# A sweep holds the lock on the stored references while it waits on the directory, and in the app that wait comes
# back to the window's thread. A rename that BLOCKED the thread for the lock would stop the sweep from ever
# finishing, and the window would freeze for good. Its update has to wait for the lock without blocking.
function DuringSweep([scriptblock]$update) {
    $f = OnPremFake; $st = Stores
    $t = New-Object UnifiedDirectoryManager.Models.UserTemplate; $t.Name = 'During a sweep'; $t.GroupDns.Add($GRP)
    $c = New-Object UnifiedDirectoryManager.Models.CloudGroupRef; $c.Id = 'm365-id'; $c.Name = 'Old cloud name'; $t.CloudGroups.Add($c)
    $st.T.Save($t, $null)
    $m = [UnifiedDirectoryManager.Services.GroupReferenceMaintenance]::new($f, $st.T, $st.S)
    $hold = [System.Threading.Tasks.TaskCompletionSource[bool]]::new()
    # Lets go by itself after 3 s, so an update that does block fails its check instead of hanging the suite.
    $f.HoldGuidLookups = [System.Threading.Tasks.Task]::WhenAny($hold.Task, [System.Threading.Tasks.Task]::Delay(3000))
    # The sweep starts with no synchronization context so that, if this thread is blocked, it can still finish
    # on a pool thread and release the lock.
    $ui = [System.Threading.SynchronizationContext]::Current
    [System.Threading.SynchronizationContext]::SetSynchronizationContext($null)
    try { $sweep = $m.UpgradeAllAsync($None) } finally { [System.Threading.SynchronizationContext]::SetSynchronizationContext($ui) }
    $running = -not $sweep.IsCompleted
    $task = & $update $m
    $waited = -not $task.IsCompleted
    $hold.SetResult($true)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not ($sweep.IsCompleted -and $task.IsCompleted) -and $sw.ElapsedMilliseconds -lt 10000) { Pump; Start-Sleep -Milliseconds 10 }
    return @{ Running = $running; Waited = $waited; Result = (Run $task); Template = $st.T.LoadAll()[0] }
}
$d = DuringSweep { param($m) $m.RetargetGroupAsync($GUID, $GRP, "CN=Sales West,OU=Groups,$DOM") }
Check 'the sweep is held open on the directory'           $true $d.Running
Check 'a rename''s retarget waits without blocking'        $true $d.Waited
Check '  then runs once the sweep is done'                1 $d.Result
Check '  and moves the template'                          "CN=Sales West,OU=Groups,$DOM" $d.Template.GroupDns[0]
$d = DuringSweep { param($m) $m.RefreshCloudGroupAsync('m365-id', 'New cloud name', $null, $null) }
Check 'a cloud rename''s refresh waits without blocking'   $true $d.Waited
Check '  then runs once the sweep is done'                'New cloud name' $d.Template.CloudGroups[0].Name

Write-Host "`n--- the editors keep GUIDs when they save ---"
# Both editors rebuild what they save from the form, which shows DNs only. Without carrying the GUIDs across,
# every save would throw away what the sweep learnt.
$st = Stores
$tpl = New-Object UnifiedDirectoryManager.Models.UserTemplate
$tpl.Name = 'Keeps GUIDs'; $tpl.GroupDns.Add($GRP); $tpl.GroupGuids[$GRP] = $GUID
$st.T.Save($tpl, $null)
$dlg = New-Object UnifiedDirectoryManager.TestSupport.InertDialogService
$editor = [UnifiedDirectoryManager.ViewModels.TemplateEditorViewModel]::new($st.T, $dlg,
    [UnifiedDirectoryManager.Services.SettingsStore]::new((Join-Path $tmp 'settings')), (New-Object UnifiedDirectoryManager.Services.AppSettings))
$editor.SelectedTemplate = @($editor.Templates | Where-Object Name -eq 'Keeps GUIDs')[0]
$editor.Description = 'edited'
$editor.SaveCommand.Execute($null)
$saved = @($st.T.LoadAll() | Where-Object Name -eq 'Keeps GUIDs')[0]
Check 'saving a template keeps its group GUIDs'           $GUID ($Links::GuidFor($saved.GroupGuids, $GRP))
Check '  as well as the edit'                             'edited' $saved.Description
$editor.NewTemplateCommand.Execute($null)
$editor.Name = 'Fresh'
$editor.SaveCommand.Execute($null)
Check '  and a new template starts with none'             0 (@($st.T.LoadAll() | Where-Object Name -eq 'Fresh')[0]).GroupGuids.Count
$stepIn = New-Object UnifiedDirectoryManager.Models.ScenarioStep
$stepIn.Action = [UnifiedDirectoryManager.Models.ScenarioActionType]::AddToGroups
$stepIn.GroupDns.Add($GRP); $stepIn.GroupDns.Add($OTHER); $stepIn.GroupGuids[$GRP] = $GUID; $stepIn.GroupGuids[$OTHER] = $OGUID
$rowVm = [UnifiedDirectoryManager.ViewModels.ScenarioStepRow]::new($dlg, $stepIn)
$rowVm.Groups.RemoveAt(1)
$out = $rowVm.ToStep()
Check 'a scenario step keeps the GUIDs of its groups'     $GUID ($Links::GuidFor($out.GroupGuids, $GRP))
Check '  and drops the one removed in the editor'         $null ($Links::GuidFor($out.GroupGuids, $OTHER))

Write-Host "`n--- a scenario run finds its groups by GUID ---"
$f = OnPremFake
$runner = [UnifiedDirectoryManager.Services.ScenarioRunner]::new($f, (New-Object UnifiedDirectoryManager.TestSupport.InertGraphService), (New-Object UnifiedDirectoryManager.TestSupport.InertExchangeService))
$sc = New-Object UnifiedDirectoryManager.Models.Scenario; $sc.Name = 'Joiner'
$step = New-Object UnifiedDirectoryManager.Models.ScenarioStep
$step.Action = [UnifiedDirectoryManager.Models.ScenarioActionType]::AddToGroups
$step.GroupDns.Add("CN=Sales Team (old),OU=Groups,$DOM"); $step.GroupGuids["CN=Sales Team (old),OU=Groups,$DOM"] = $GUID
$sc.Steps.Add($step)
$user = New-Object UnifiedDirectoryManager.Models.AdObjectRow
$user.DistinguishedName = "CN=Bob,OU=Staff,$DOM"; $user.Name = 'Bob'; $user.Type = [UnifiedDirectoryManager.Models.AdObjectType]::User
$log = [System.Collections.Generic.List[string]]::new()
$res = Run ($runner.RunAsync($sc, [UnifiedDirectoryManager.Models.AdObjectRow[]]@($user), $null, $None, $log, $null))
Check 'a renamed group is added to where it is now'       $GRP (Safe { $f.Writes[0].Item2[0].Values[0] })
Check '  and the step succeeds'                           $true (Safe { $res.Items[0].Success })
Check '  without editing the scenario it was given'       "CN=Sales Team (old),OU=Groups,$DOM" $step.GroupDns[0]
$f2 = OnPremFake
$step.GroupDns.Clear(); $step.GroupGuids.Clear()
$step.GroupDns.Add($GRP); $step.GroupGuids[$GRP] = $GUID
$step.GroupDns.Add("CN=Gone,$DOM"); $step.GroupGuids["CN=Gone,$DOM"] = 'deadbeef-0000-0000-0000-000000000000'
$runner2 = [UnifiedDirectoryManager.Services.ScenarioRunner]::new($f2, (New-Object UnifiedDirectoryManager.TestSupport.InertGraphService), (New-Object UnifiedDirectoryManager.TestSupport.InertExchangeService))
$res = Run ($runner2.RunAsync($sc, [UnifiedDirectoryManager.Models.AdObjectRow[]]@($user), $null, $None, $log, $null))
Check 'a deleted group fails the step'                    $false (Safe { $res.Items[0].Success })
Check '  naming the group'                                $true ([string](Safe { $res.Items[0].Error }) -like '*Gone*')
Check '  after the groups that exist were still added'    $GRP (Safe { ($f2.Writes[0].Item2[0].Values -join ';') })

Write-Host "`n--- on-prem group: checks ---"
$f = OnPremFake; $graph = New-Object UnifiedDirectoryManager.TestSupport.RenameGraphService
$ex = New-Object UnifiedDirectoryManager.TestSupport.RenameExchangeService
$st = Stores; $maint = [UnifiedDirectoryManager.Services.GroupReferenceMaintenance]::new($f, $st.T, $st.S)
$svc = [UnifiedDirectoryManager.Services.GroupRenameService]::new($f, $graph, $ex, $maint)
$s = Run ($svc.LoadAsync((Source $Origin::OnPremDn $GRP), $None))
Check 'an on-prem group is renamed in AD'                 'OnPremAd' ([string]$s.Kind)
Check '  read with every name it has'                     'Sales Team|Sales Team|Sales Team' "$($s.Name)|$($s.SamAccountName)|$($s.DisplayName)"
Check '  its primary address from proxyAddresses'         'salesteam@contoso.com' $s.PrimaryAddress
Check '  and its GUID and SID'                            "$GUID|$SID" "$($s.ObjectGuid)|$($s.Sid)"
[void]$f.ExistingDns.Add("CN=Marketing Team,OU=Groups,$DOM")
$c = Run ($svc.CheckAsync($s, (Request 'Marketing Team' 'Marketing Team' 'Marketing Team' $null), $None))
Check 'a name taken in the same OU is refused'            $true ((@($c.Problems) -join ' ') -like '*already called*')
$c = Run ($svc.CheckAsync($s, (Request 'sales team' 'Sales Team' 'sales team' $null), $None))
Check 'changing only the case is not a clash'             0 $c.Problems.Count
Check '  and is still a change'                           $true ($c.Changes.Count -ge 1)
[void]$f.ExistingSams.Add('Sales East')
$c = Run ($svc.CheckAsync($s, (Request 'Sales East' 'Sales East' 'Sales East' $null), $None))
Check 'a pre-Windows 2000 name in use is refused'         $true ((@($c.Problems) -join ' ') -like '*already used*')
$c = Run ($svc.CheckAsync($s, (Request 'Sales East' '' 'Sales East2' $null), $None))
Check 'a mail-enabled group must keep a display name'     $true ((@($c.Problems) -join ' ') -like '*needs a display name*')
$f.AddressOwners['saleseast@contoso.com'] = [System.Collections.Generic.List[string]]@("CN=Someone,OU=Staff,$DOM")
$c = Run ($svc.CheckAsync($s, (Request 'Sales East' 'Sales East' 'Sales East2' 'saleseast@contoso.com'), $None))
Check 'an address held in AD is refused'                  $true ((@($c.Problems) -join ' ') -like '*already belongs to Someone*')
$f.AddressOwners.Clear()
$graph.AddressOwners['saleseast@contoso.com'] = [System.Collections.Generic.List[UnifiedDirectoryManager.Models.CloudAddressOwner]]@(
    [UnifiedDirectoryManager.Models.CloudAddressOwner]::new('cloud-1', 'East Sales (cloud)', 'Group', $null))
$c = Run ($svc.CheckAsync($s, (Request 'Sales East' 'Sales East' 'Sales East2' 'saleseast@contoso.com'), $None))
Check 'an address held only in Microsoft 365 is refused'  $true ((@($c.Problems) -join ' ') -like '*East Sales (cloud)*Microsoft 365*')
$graph.AddressOwners['saleseast@contoso.com'] = [System.Collections.Generic.List[UnifiedDirectoryManager.Models.CloudAddressOwner]]@(
    [UnifiedDirectoryManager.Models.CloudAddressOwner]::new('synced-copy', 'Sales Team', 'Group', $SID))
$c = Run ($svc.CheckAsync($s, (Request 'Sales East' 'Sales East' 'Sales East2' 'saleseast@contoso.com'), $None))
Check '  but the group''s own synced copy is not a clash' 0 $c.Problems.Count
$ex.Held.Add([System.ValueTuple]::Create([UnifiedDirectoryManager.Models.RecipientConflict]::new('contact-guid', 'Partner Sales', 'MailContact', 'Address', $null), 'saleseast@contoso.com'))
$c = Run ($svc.CheckAsync($s, (Request 'Sales East' 'Sales East' 'Sales East2' 'saleseast@contoso.com'), $None))
Check 'an address only Exchange knows (a contact) is refused' $true ((@($c.Problems) -join ' ') -like '*Partner Sales*Exchange Online*')
$ex.Held.Clear()
$graph.SignedIn = $false
$c = Run ($svc.CheckAsync($s, (Request 'Sales East' 'Sales East' 'Sales East2' 'saleseast@contoso.com'), $None))
Check 'signed out, the cloud check is said to be skipped' $true ((@($c.Warnings) -join ' ') -like '*not checked*')
$graph.SignedIn = $true
Check '  and the change list says the old address stays'  $true ((@($c.Changes | ForEach-Object { $_.After }) -join ' ') -like '*kept as a secondary*')
$c = Run ($svc.CheckAsync($s, (Request 'Sales Team' 'Sales Team' 'Sales Team' $null), $None))
Check 'an unchanged request has nothing to apply'         $false $c.CanApply
# Making one of the group's own addresses its primary. Its copy in the cloud already holds that address, in Entra
# and in Exchange, and must not be taken for another recipient.
$f.AddressOwners['sales@contoso.com'] = [System.Collections.Generic.List[string]]@($GRP)
$graph.AddressOwners['sales@contoso.com'] = [System.Collections.Generic.List[UnifiedDirectoryManager.Models.CloudAddressOwner]]@(
    [UnifiedDirectoryManager.Models.CloudAddressOwner]::new('synced-copy', 'Sales Team', 'Group', $SID))
$ex.Held.Add([System.ValueTuple]::Create([UnifiedDirectoryManager.Models.RecipientConflict]::new('copy-guid', 'Sales Team', 'MailUniversalSecurityGroup', 'Address', 'synced-copy'), 'sales@contoso.com'))
$promote = Request 'Sales Team' 'Sales Team' 'Sales Team' 'sales@contoso.com'
$c = Run ($svc.CheckAsync($s, $promote, $None))
Check 'promoting the group''s own alias is not a clash'   0 $c.Problems.Count
Check '  nor a warning about Exchange'                    $false ((@($c.Warnings) -join ' ') -like '*Exchange Online lists*')
$graph.SignedIn = $false
$c = Run ($svc.CheckAsync($s, $promote, $None))
Check '  signed out of Entra, it is only a warning'       '0|True' "$($c.Problems.Count)|$((@($c.Warnings) -join ' ') -like '*own synced copy*')"
$ex.Held.Add([System.ValueTuple]::Create([UnifiedDirectoryManager.Models.RecipientConflict]::new('other-guid', 'Someone Else', 'MailContact', 'Address', $null), 'elsewhere@contoso.com'))
$c = Run ($svc.CheckAsync($s, (Request 'Sales Team' 'Sales Team' 'Sales Team' 'elsewhere@contoso.com'), $None))
Check '  but a new address held in Exchange is refused'   $true ((@($c.Problems) -join ' ') -like '*Someone Else*Exchange Online*')
$graph.SignedIn = $true
$c = Run ($svc.CheckAsync($s, (Request 'Sales Team' 'Sales Team' 'Sales Team' 'elsewhere@contoso.com'), $None))
Check '  signed in as well'                               $true ((@($c.Problems) -join ' ') -like '*Someone Else*Exchange Online*')
# Microsoft 365 answered, and its copy of the group does not have the address (sync set it aside over a clash):
# then whatever Exchange lists with it is another recipient, even though the group has the address in AD.
$graph.AddressOwners.Remove('sales@contoso.com') | Out-Null
$ex.Held.Clear()
$ex.Held.Add([System.ValueTuple]::Create([UnifiedDirectoryManager.Models.RecipientConflict]::new('contact-2', 'Partner Desk', 'MailContact', 'Address', $null), 'sales@contoso.com'))
$c = Run ($svc.CheckAsync($s, $promote, $None))
Check '  and an alias another recipient holds is refused' $true ((@($c.Problems) -join ' ') -like '*Partner Desk*Exchange Online*')
$ex.Held.Clear(); $f.AddressOwners.Clear()

Write-Host "`n--- on-prem group: the rename ---"
$st.T.Save((& { $t = New-Object UnifiedDirectoryManager.Models.UserTemplate; $t.Name = 'Uses Sales'; $t.GroupDns.Add($GRP); $t }), $null)
$o = Run ($svc.ApplyAsync($s, (Request 'Sales East' 'Sales East' 'Sales East2' 'saleseast@contoso.com'), $None))
Check 'the names go in ONE modify'                        1 $f.Writes.Count
$w = @($f.Writes[0].Item2 | ForEach-Object { $_.LdapName })
Check '  sAMAccountName, displayName, mail, proxyAddresses' 'sAMAccountName,displayName,mail,proxyAddresses' ($w -join ',')
$px = @($f.Writes[0].Item2 | Where-Object { $_.LdapName -eq 'proxyAddresses' })[0].Values
CheckExact '  with the new primary first'                 'SMTP:saleseast@contoso.com' $px[0]
Check '  the old one demoted, not dropped'                $true ($px -ccontains 'smtp:salesteam@contoso.com')
Check '  and the X500 address kept'                       $true ($px -ccontains 'X500:/o=Old/cn=Sales')
Check 'then the name in the tree, by modify-DN'           "$GRP>Sales East" (Safe { "$($f.Renames[0].Item1)>$($f.Renames[0].Item2)" })
Check 'the outcome is the new DN'                         "CN=Sales East,OU=Groups,$DOM" $o.Identity
Check '  and it succeeded'                                $true $o.Succeeded
Check 'templates that use it follow it'                   "CN=Sales East,OU=Groups,$DOM" ($st.T.LoadAll() | Where-Object Name -eq 'Uses Sales').GroupDns[0]
Check '  and the operator is told'                        $true ((@($o.Notes) -join ' ') -like '*Updated 1 template*')
Check '  as is that the old address still works'          $true ((@($o.Notes) -join ' ') -like '*salesteam@contoso.com still delivers*')

$f = OnPremFake; [void]$f.FailWritesFor.Add($GRP)
$svc = [UnifiedDirectoryManager.Services.GroupRenameService]::new($f, $graph, $ex, $null)
$s = Run ($svc.LoadAsync((Source $Origin::OnPremDn $GRP), $None))
$o = Run ($svc.ApplyAsync($s, (Request 'Sales East' 'Sales East' 'Sales East' $null), $None))
Check 'a refused modify renames nothing'                  0 $f.Renames.Count
Check '  and says nothing was changed'                    $true ([string]$o.Error -like 'Nothing was changed*')
$f = OnPremFake; [void]$f.FailRenamesFor.Add($GRP)
$svc = [UnifiedDirectoryManager.Services.GroupRenameService]::new($f, $graph, $ex, $null)
$s = Run ($svc.LoadAsync((Source $Origin::OnPremDn $GRP), $None))
$o = Run ($svc.ApplyAsync($s, (Request 'Sales East' 'Sales East' 'Sales East' $null), $None))
Check 'a refused modify-DN after the modify'              $true $o.NamePending
Check '  says exactly that'                               $true ([string]$o.Error -like '*other names were changed*not*')
Check '  and keeps the old DN'                            $GRP $o.Identity
# The group is renamed before any template is touched, so a template that cannot be saved must not turn the rename
# into a reported failure: the operator would retry a rename that already happened.
$f = OnPremFake; $st = Stores
$st.T.Save((& { $t = New-Object UnifiedDirectoryManager.Models.UserTemplate; $t.Name = 'Read-only'; $t.GroupDns.Add($GRP); $t }), $null)
Get-ChildItem $st.T.TemplatesDirectory -File | ForEach-Object { $_.IsReadOnly = $true }
$svc = [UnifiedDirectoryManager.Services.GroupRenameService]::new($f, $graph, $ex, [UnifiedDirectoryManager.Services.GroupReferenceMaintenance]::new($f, $st.T, $st.S))
$s = Run ($svc.LoadAsync((Source $Origin::OnPremDn $GRP), $None))
$o = Run ($svc.ApplyAsync($s, (Request 'Sales East' 'Sales East' 'Sales East' $null), $None))
Check 'a template that cannot be saved does not fail the rename' $true (Safe { $o.Succeeded })
Check '  which reports the group where it now is'         "CN=Sales East,OU=Groups,$DOM" (Safe { $o.Identity })

Write-Host "`n--- a synced group opened from the cloud is renamed on-prem ---"
$f = OnPremFake; $graph = New-Object UnifiedDirectoryManager.TestSupport.RenameGraphService
$graph.Groups['synced-id'] = [UnifiedDirectoryManager.Models.CloudGroupNaming]::new('synced-id', 'Sales Team', 'salesteam@contoso.com', 'salesteam', [string[]]@(), 'Mail-enabled security', $false, $true, $SID)
$svc = [UnifiedDirectoryManager.Services.GroupRenameService]::new($f, $graph, $ex, $null)
$s = Run ($svc.LoadAsync((Source $Origin::EntraGroupId 'synced-id'), $None))
Check 'it is found in AD by its SID'                      $GRP (Safe { $s.Identity })
Check '  and renamed there'                               'OnPremAd' ([string](Safe { $s.Kind }))
Check '  saying why'                                      $true (Safe { $s.RedirectedFromCloud })
$graph.Groups['synced-id'] = [UnifiedDirectoryManager.Models.CloudGroupNaming]::new('synced-id', 'Sales Team', $null, 'x', [string[]]@(), 'Security', $false, $true, 'S-1-5-21-404')
$s = Run ($svc.LoadAsync((Source $Origin::EntraGroupId 'synced-id'), $None))
Check 'no on-prem match is said plainly'                  $true ([string]$s -like '*no group with its SID*')

Write-Host "`n--- Entra security and Microsoft 365 groups ---"
$graph = New-Object UnifiedDirectoryManager.TestSupport.RenameGraphService
$graph.Groups['sec-id'] = [UnifiedDirectoryManager.Models.CloudGroupNaming]::new('sec-id', 'App Admins', $null, 'appadmins', [string[]]@(), 'Security', $false, $false, $null)
$graph.Groups['team-id'] = [UnifiedDirectoryManager.Models.CloudGroupNaming]::new('team-id', 'Project X', 'projectx@contoso.com', 'projectx', [string[]]@('SMTP:projectx@contoso.com'), 'Microsoft 365', $true, $false, $null)
$ex = New-Object UnifiedDirectoryManager.TestSupport.RenameExchangeService
$ex.UnifiedAddresses['projectx@contoso.com'] = [System.Collections.Generic.List[string]]@('SMTP:projectx@contoso.com', 'smtp:projectx@contoso.onmicrosoft.com')
$st = Stores; $f = OnPremFake; $maint = [UnifiedDirectoryManager.Services.GroupReferenceMaintenance]::new($f, $st.T, $st.S)
$tplC = New-Object UnifiedDirectoryManager.Models.UserTemplate; $tplC.Name = 'Cloud'
$cg = New-Object UnifiedDirectoryManager.Models.CloudGroupRef; $cg.Id = 'team-id'; $cg.Name = 'Project X'; $tplC.CloudGroups.Add($cg); $st.T.Save($tplC, $null)
$svc = [UnifiedDirectoryManager.Services.GroupRenameService]::new($f, $graph, $ex, $maint)
$s = Run ($svc.LoadAsync((Source $Origin::EntraGroupId 'sec-id'), $None))
Check 'a cloud-only security group is renamed in Entra'   'EntraSecurity' ([string]$s.Kind)
$graph.GroupIdsByName['Project X'] = [System.Collections.Generic.List[string]]@('team-id')
$c = Run ($svc.CheckAsync($s, (Request 'Project X' 'Project X' $null $null), $None))
Check 'a name another group has is only a warning'        0 $c.Problems.Count
Check '  but it is said'                                  $true ((@($c.Warnings) -join ' ') -like '*already called*')
$o = Run ($svc.ApplyAsync($s, (Request 'App Admins EU' 'App Admins EU' $null $null), $None))
Check 'Graph is asked for the display name only'          'displayName=App Admins EU' (Safe { ($graph.GroupUpdates[0].Item2.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';' })
Check '  and Exchange is not involved'                    0 $ex.UnifiedPrimaryChanges.Count

$s = Run ($svc.LoadAsync((Source $Origin::EntraGroupId 'team-id'), $None))
Check 'a Team is a Microsoft 365 group'                   'Microsoft365|True' "$($s.Kind)|$($s.IsTeam)"
$ex.Held.Add([System.ValueTuple]::Create([UnifiedDirectoryManager.Models.RecipientConflict]::new('team-exo-guid', 'Project X', 'GroupMailbox', 'Address', 'team-id'), 'projectx@contoso.onmicrosoft.com'))
$c = Run ($svc.CheckAsync($s, (Request 'Project X' 'Project X' $null 'projectx@contoso.onmicrosoft.com'), $None))
Check '  whose own alias in Exchange is not a clash'      0 $c.Problems.Count
$ex.Held.Clear()
$graph.NamingPolicy = { param($n) "GRP_$n" }
$o = Run ($svc.ApplyAsync($s, (Request 'Project Y' 'Project Y' $null 'projecty@contoso.com'), $None))
Check 'the name Entra stored is what is reported'         'GRP_Project Y' $o.ActualName
Check '  with the naming policy named'                    $true ((@($o.Notes) -join ' ') -like '*naming policy*GRP_Project Y*')
Check 'the address goes through Exchange'                 'projectx@contoso.com>projecty@contoso.com' (Safe { "$($ex.UnifiedPrimaryChanges[0].Item1)>$($ex.UnifiedPrimaryChanges[0].Item2)" })
Check '  and the new primary is read from its answer'     'projecty@contoso.com' $o.ActualPrimaryAddress
Check '  the old one confirmed as still delivering'       $true ((@($o.Notes) -join ' ') -like '*projectx@contoso.com still delivers*')
Check '  the Team''s SharePoint address said unchanged'   $true ((@($o.Notes) -join ' ') -like '*SharePoint*unchanged*')
Check 'templates show the group''s new name'              'GRP_Project Y' ($st.T.LoadAll()[0].CloudGroups[0].Name)
$graph.NamingPolicy = { param($n) $n }
[void]$ex.FailUnifiedFor.Add('projectx@contoso.com')
$graph.Groups['team-id'] = [UnifiedDirectoryManager.Models.CloudGroupNaming]::new('team-id', 'Project X', 'projectx@contoso.com', 'projectx', [string[]]@('SMTP:projectx@contoso.com'), 'Microsoft 365', $true, $false, $null)
$s = Run ($svc.LoadAsync((Source $Origin::EntraGroupId 'team-id'), $None))
$o = Run ($svc.ApplyAsync($s, (Request 'Project Z' 'Project Z' $null 'projectz@contoso.com'), $None))
Check 'a refused address after the rename is not a success' $false $o.Succeeded
Check '  and says the name WAS changed'                   $true ([string]$o.Error -like 'The name was changed, but the email address was not*')

Write-Host "`n--- Exchange distribution lists ---"
$ex = New-Object UnifiedDirectoryManager.TestSupport.RenameExchangeService
$ex.Groups['dl-guid'] = [UnifiedDirectoryManager.Models.DistributionGroupNaming]::new('dl-guid', 'All Sales', 'All Sales', 'allsales',
    'allsales@contoso.com', [string[]]@('SMTP:allsales@contoso.com', 'smtp:allsales@contoso.onmicrosoft.com'), $false, 'dl-entra-id', 'MailUniversalDistributionGroup')
$graph = New-Object UnifiedDirectoryManager.TestSupport.RenameGraphService
$graph.Groups['dl-entra-id'] = [UnifiedDirectoryManager.Models.CloudGroupNaming]::new('dl-entra-id', 'All Sales', 'allsales@contoso.com', 'allsales', [string[]]@(), 'Distribution', $false, $false, $null)
$st = Stores; $f = OnPremFake; $maint = [UnifiedDirectoryManager.Services.GroupReferenceMaintenance]::new($f, $st.T, $st.S)
$tplD = New-Object UnifiedDirectoryManager.Models.UserTemplate; $tplD.Name = 'DL'
$dl = New-Object UnifiedDirectoryManager.Models.DistributionGroupRef; $dl.Id = 'dl-entra-id'; $dl.Name = 'All Sales'; $dl.Smtp = 'allsales@contoso.com'; $tplD.DistributionGroups.Add($dl); $st.T.Save($tplD, $null)
$svc = [UnifiedDirectoryManager.Services.GroupRenameService]::new($f, $graph, $ex, $maint)
$s = Run ($svc.LoadAsync((Source $Origin::EntraGroupId 'dl-entra-id'), $None))
Check 'a list opened from the Entra list goes to Exchange' 'ExchangeDistribution' ([string](Safe { $s.Kind }))
Check '  addressed by its Exchange GUID'                  'dl-guid' (Safe { $s.Identity })
$ex.Held.Add([System.ValueTuple]::Create([UnifiedDirectoryManager.Models.RecipientConflict]::new('other-guid', 'Sales Room', 'RoomMailbox', 'Name', 'room-entra-id'), 'All Sales East'))
$c = Run ($svc.CheckAsync($s, (Request 'All Sales East' 'All Sales East' $null $null), $None))
Check 'a name another recipient holds is refused'         $true ((@($c.Problems) -join ' ') -like '*Sales Room*')
$ex.Held.Clear()
# Exchange reporting the list itself as a holder (a name it already took in an earlier, partial attempt).
$ex.Held.Add([System.ValueTuple]::Create([UnifiedDirectoryManager.Models.RecipientConflict]::new('dl-guid', 'All Sales', 'MailUniversalDistributionGroup', 'Name', 'dl-entra-id'), 'All Sales Two'))
$c = Run ($svc.CheckAsync($s, (Request 'All Sales Two' 'All Sales Two' $null $null), $None))
Check '  but the list itself is not a clash'              0 $c.Problems.Count
$ex.Held.Clear()
$ex.NamingPolicy = { param($n) "DL_$n" }
$o = Run ($svc.ApplyAsync($s, (Request 'All Sales East' 'All Sales East' $null 'allsaleseast@contoso.com'), $None))
Check 'name and display name go in one rename'            'dl-guid|All Sales East|All Sales East|allsaleseast@contoso.com' (Safe { $r0 = $ex.Renames[0]; "$($r0.Item1)|$($r0.Item2)|$($r0.Item3)|$($r0.Item4)" })
Check '  and what Exchange set is what is reported'       'DL_All Sales East' $o.ActualDisplayName
Check '  naming Exchange''s naming policy'                $true ((@($o.Notes) -join ' ') -like '*distribution group naming policy*')
Check '  the old address still delivering'                $true ((@($o.Notes) -join ' ') -like '*allsales@contoso.com still delivers*')
Check 'templates add members through the new address'     'allsaleseast@contoso.com' ($st.T.LoadAll()[0].DistributionGroups[0].Smtp)
Check '  under the new name'                              'DL_All Sales East' ($st.T.LoadAll()[0].DistributionGroups[0].Name)
$ex.Groups['synced-dl'] = [UnifiedDirectoryManager.Models.DistributionGroupNaming]::new('synced-dl', 'Sales Team', 'Sales Team', 'salesteam',
    'salesteam@contoso.com', [string[]]@('SMTP:salesteam@contoso.com'), $true, 'synced-entra', 'MailUniversalSecurityGroup')
$graph.Groups['synced-entra'] = [UnifiedDirectoryManager.Models.CloudGroupNaming]::new('synced-entra', 'Sales Team', 'salesteam@contoso.com', 'salesteam', [string[]]@(), 'Mail-enabled security', $false, $true, $SID)
$s = Run ($svc.LoadAsync((Source $Origin::ExchangeIdentity 'salesteam@contoso.com'), $None))
Check 'a synced list from the Exchange list goes to AD'   "OnPremAd|$GRP" ("$(Safe { $s.Kind })|$(Safe { $s.Identity })")

Write-Host "`n--- the dialog's view model ---"
$f = OnPremFake; $graph = New-Object UnifiedDirectoryManager.TestSupport.RenameGraphService
$ex = New-Object UnifiedDirectoryManager.TestSupport.RenameExchangeService
$dlg = New-Object UnifiedDirectoryManager.TestSupport.InertDialogService
$svc = [UnifiedDirectoryManager.Services.GroupRenameService]::new($f, $graph, $ex, $null)
$synced = [System.Collections.Generic.List[string]]::new()
$sync = [System.Func[System.Threading.CancellationToken, System.Threading.Tasks.Task[string]]]{
    param($ct) $synced.Add('sync'); [System.Threading.Tasks.Task]::FromResult('A delta sync was started.') }.GetNewClosure()
$vm = [UnifiedDirectoryManager.ViewModels.RenameGroupViewModel]::new($svc, $dlg, (Source $Origin::OnPremDn $GRP), $sync)
[void](Run ($vm.LoadAsync()))
Check 'the form opens with the current names'             'Sales Team|Sales Team|Sales Team' "$($vm.Name)|$($vm.SamAccountName)|$($vm.DisplayName)"
Check '  showing the on-prem fields'                      'True|True|True' "$($vm.ShowName)|$($vm.ShowSam)|$($vm.CanChangeAddress)"
$vm.Name = 'Sales East'
Check 'the derived names follow the new name'             'Sales East|Sales East' "$($vm.SamAccountName)|$($vm.DisplayName)"
$vm.SamAccountName = 'SalesE'
$vm.Name = 'Sales EMEA'
Check '  until one is edited by hand'                     'SalesE|Sales EMEA' "$($vm.SamAccountName)|$($vm.DisplayName)"
$vm.ChangeAddress = $true
Check 'ticking the address suggests one from the name'    'salesemea@contoso.com' $vm.NewAddress
$vm.Name = 'Sales Nordic'
Check '  and keeps following it'                          'salesnordic@contoso.com' $vm.NewAddress
$vm.ChangeAddress = $false
Check 'unticking puts the current address back'           'salesteam@contoso.com' $vm.NewAddress
$dlg.ConfirmAnswer = $false
[void](Run ($vm.RenameCommand.ExecuteAsync($null)))
Check 'declining the confirmation writes nothing'         '0|0' "$($f.Writes.Count)|$($f.Renames.Count)"
Check '  and the confirmation listed the changes'         $true ((@($dlg.ConfirmationLines[0]) -join ' ') -like '*Group name: Sales Team*Sales Nordic*')
$dlg.ConfirmAnswer = $true
[void](Run ($vm.RenameCommand.ExecuteAsync($null)))
Check 'confirming renames the group'                      "CN=Sales Nordic,OU=Groups,$DOM" (Safe { $vm.Outcome.Identity })
Check '  and the dialog shows it is done'                 'True|Close|False' "$($vm.IsDone)|$($vm.CloseLabel)|$($vm.ShowRenameButton)"
Check '  with what AD now holds'                          $true ((@($vm.ResultLines) -join ' ') -like '*Name: Sales Nordic*')
Check 'an on-prem rename offers Sync now'                 $true $vm.ShowSyncNow
[void](Run ($vm.SyncNowCommand.ExecuteAsync($null)))
Check '  which runs the sync'                             1 $synced.Count
Check '  and says what happened'                          'A delta sync was started.' $vm.Status
$vm2 = [UnifiedDirectoryManager.ViewModels.RenameGroupViewModel]::new($svc, $dlg, (Source $Origin::OnPremDn $GRP), $null)
Check 'no Entra Connect server, no Sync now'              $false $vm2.ShowSyncNow
$fBad = OnPremFake; $fBad.ExistingDns.Add("CN=Taken,OU=Groups,$DOM") | Out-Null
$vm3 = [UnifiedDirectoryManager.ViewModels.RenameGroupViewModel]::new(
    [UnifiedDirectoryManager.Services.GroupRenameService]::new($fBad, $graph, $ex, $null), $dlg, (Source $Origin::OnPremDn $GRP), $null)
[void](Run ($vm3.LoadAsync()))
$before = $dlg.Confirmations.Count
$vm3.Name = 'Taken'
[void](Run ($vm3.RenameCommand.ExecuteAsync($null)))
Check 'a refused check is shown, not confirmed'           $before $dlg.Confirmations.Count
Check '  with the reason'                                 $true ($vm3.Status -like '*already called*')
# A rename that stops halfway: the other names and the address were written, the name in the tree was refused.
# The dialog stays open with what was typed, and trying again finishes only what is left.
$fHalf = OnPremFake; $fHalf.StoreWrites = $true; [void]$fHalf.FailRenamesFor.Add($GRP)
$vmH = [UnifiedDirectoryManager.ViewModels.RenameGroupViewModel]::new(
    [UnifiedDirectoryManager.Services.GroupRenameService]::new($fHalf, $graph, $ex, $null), $dlg, (Source $Origin::OnPremDn $GRP), $null)
[void](Run ($vmH.LoadAsync()))
$vmH.Name = 'Sales Nordic'; $vmH.ChangeAddress = $true
$dlg.ConfirmAnswer = $true
[void](Run ($vmH.RenameCommand.ExecuteAsync($null)))
Check 'a half-finished rename leaves Rename offered'      'False|True' "$($vmH.IsDone)|$($vmH.ShowRenameButton)"
Check '  saying what was and was not done'                $true ([string]$vmH.Status -like '*other names were changed*')
Check '  with the names typed still there'                'Sales Nordic|Sales Nordic|Sales Nordic' "$($vmH.Name)|$($vmH.SamAccountName)|$($vmH.DisplayName)"
$fHalf.FailRenamesFor.Clear()
[void](Run ($vmH.RenameCommand.ExecuteAsync($null)))
Check 'trying again renames the group'                    "CN=Sales Nordic,OU=Groups,$DOM" (Safe { $vmH.Outcome.Identity })
Check '  without writing the other names a second time'   1 $fHalf.Writes.Count
Check '  and the first try''s address is the primary'      'salesnordic@contoso.com' (Safe { $vmH.Outcome.ActualPrimaryAddress })

Write-Host "`n--- what the operator sees ---"
$app = [UnifiedDirectoryManager.App]::new(); $app.ShutdownMode = 'OnExplicitShutdown'; $app.InitializeComponent()
function ShowWindow($vm) {
    $w = [UnifiedDirectoryManager.Views.Dialogs.RenameGroupWindow]::new()
    $w.DataContext = $vm; $w.Left = -4000; $w.ShowActivated = $false; $w.Show()
    for ($i = 0; $i -lt 20; $i++) { Pump }
    return $w
}
$V = [System.Windows.Visibility]
$f = OnPremFake
$vm = [UnifiedDirectoryManager.ViewModels.RenameGroupViewModel]::new(
    [UnifiedDirectoryManager.Services.GroupRenameService]::new($f, $graph, $ex, $null), $dlg, (Source $Origin::OnPremDn $GRP), $sync)
$w = ShowWindow $vm
Check 'on-prem: name, pre-Windows 2000 name, display name' "$($V::Visible)|$($V::Visible)|$($V::Visible)" "$($w.FindName('NameBox').Visibility)|$($w.FindName('SamBox').Visibility)|$($w.FindName('DisplayNameBox').Visibility)"
Check '  and the address option'                          $V::Visible $w.FindName('ChangeAddressBox').Visibility
Check '  the kind is named'                               $true ([string]$w.FindName('KindText').Text -like 'Active Directory group*')
Check '  Rename offered, Sync now not yet'                "$($V::Visible)|$($V::Collapsed)" "$($w.FindName('RenameButton').Visibility)|$($w.FindName('SyncNowButton').Visibility)"
$vm.Name = 'Sales Nordic'
[void](Run ($vm.RenameCommand.ExecuteAsync($null))); for ($i = 0; $i -lt 5; $i++) { Pump }
Check 'after the rename: the result, Close, and Sync now' "$($V::Visible)|Close|$($V::Visible)" "$($w.FindName('ResultPanel').Visibility)|$($w.FindName('CloseButton').Content)|$($w.FindName('SyncNowButton').Visibility)"
Check '  and the form can no longer be edited'            $false $w.FindName('FormGrid').IsEnabled
$w.Close(); Pump

$graph2 = New-Object UnifiedDirectoryManager.TestSupport.RenameGraphService
$graph2.Groups['sec-id'] = [UnifiedDirectoryManager.Models.CloudGroupNaming]::new('sec-id', 'App Admins', $null, 'appadmins', [string[]]@(), 'Security', $false, $false, $null)
$vm = [UnifiedDirectoryManager.ViewModels.RenameGroupViewModel]::new(
    [UnifiedDirectoryManager.Services.GroupRenameService]::new($f, $graph2, $ex, $null), $dlg, (Source $Origin::EntraGroupId 'sec-id'), $sync)
$w = ShowWindow $vm
Check 'an Entra group: only the display name'             "$($V::Collapsed)|$($V::Collapsed)|$($V::Visible)" "$($w.FindName('NameBox').Visibility)|$($w.FindName('SamBox').Visibility)|$($w.FindName('DisplayNameBox').Visibility)"
Check '  and no address to change'                        $V::Collapsed $w.FindName('ChangeAddressBox').Visibility
$w.Close(); Pump

# The edit pane offers Rename… for a group, and not for a user.
$pf = OnPremFake
$pane = [UnifiedDirectoryManager.ViewModels.EditPaneViewModel]::new($pf, $dlg, [System.Action[string]]{ param($m) }, (New-Object UnifiedDirectoryManager.TestSupport.InertGraphService), (New-Object UnifiedDirectoryManager.TestSupport.InertExchangeService))
[void](Run ($pane.LoadAsync($GRP, [UnifiedDirectoryManager.Models.AdObjectType]::Group)))
$view = [UnifiedDirectoryManager.Views.Controls.EditPaneView]::new(); $view.DataContext = $pane
$host2 = [System.Windows.Window]::new(); $host2.Content = $view; $host2.Left = -4000; $host2.ShowActivated = $false; $host2.Show(); Pump; Pump
Check 'the edit pane offers Rename… for a group'          $V::Visible (Safe { $view.FindName('RenameButton').Visibility })
$newDn = "CN=Sales Nordic,OU=Groups,$DOM"
$pf.Objects[$newDn] = $pf.Objects[$GRP]
$dlg.RenameOutcome = [UnifiedDirectoryManager.Models.GroupRenameOutcome]::new($true, $newDn, 'Sales Nordic', 'Sales Nordic', $null, [string[]]@(), $null, $false)
[void](Run ($pane.RenameCommand.ExecuteAsync($null))); Pump
Check '  and follows the group to its new DN'             $newDn $pane.CurrentDn
Check '  having opened the dialog for the old one'        $GRP (Safe { $dlg.RenameRequests[$dlg.RenameRequests.Count - 1].Identity })
$host2.Close(); Pump

Write-Host "`n--- where a cloud group's Rename… is offered ---"
$RowT = [UnifiedDirectoryManager.Models.CloudObjectRow]
function CloudRow([string]$id, [string]$name) {
    $r = $RowT::new()
    $RowT.GetProperty('Id').SetValue($r, $id)
    $RowT.GetProperty('DisplayName').SetValue($r, $name)
    $RowT.GetProperty('Kind').SetValue($r, [UnifiedDirectoryManager.Models.CloudObjectKind]::Group)
    return $r
}
$quiet = New-Object UnifiedDirectoryManager.TestSupport.InertGraphService
$quietEx = New-Object UnifiedDirectoryManager.TestSupport.InertExchangeService
$listPane = [UnifiedDirectoryManager.ViewModels.CloudObjectDetailViewModel]::new($quiet, $quietEx, $dlg)
$listPane.SetTarget((CloudRow 'g1' 'App Admins')); Pump
Check 'a group in a cloud list offers Rename…'            $true $listPane.CanRename
$tab = [UnifiedDirectoryManager.ViewModels.CloudTabViewModel]::new($quiet, $quietEx, $dlg)
$tab.Detail.SetTarget((CloudRow 'g1' 'App Admins')); Pump
Check '  but not inside an on-prem object''s Cloud tab'   $false $tab.Detail.CanRename

Write-Host "`n--- a cloud distribution list in the Entra list is read-only there ---"
$Rules = [UnifiedDirectoryManager.Services.CloudPropertyRules]
$GroupKind = [UnifiedDirectoryManager.Models.CloudObjectKind]::Group
$p = $Rules::Make($GroupKind, $false, 'displayName', 'Display name', 'All Sales', $true)
Check 'its display name is not offered for editing'       'SystemReadOnly' ([string]$p.Editability)
Check '  and says where it is managed'                    $true ([string]$p.Tooltip -like '*Exchange Online*Rename*')
$p = $Rules::Make($GroupKind, $false, 'displayName', 'Display name', 'App Admins', $false)
Check 'a security group''s still is'                      'Editable' ([string]$p.Editability)
$p = $Rules::Make($GroupKind, $true, 'displayName', 'Display name', 'Sales Team', $true)
Check 'a synced list keeps the on-prem explanation'       'OnPremMastered' ([string]$p.Editability)

}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "pass=$pass fail=$fail"
if ($fail -gt 0) { exit 1 }
