<#
.SYNOPSIS
  Regression tests for F7: reconfiguring the tenant kept the previous tenant's signed-in identity.

.DESCRIPTION
  Configure() reused the authentication record with "??=", which never replaces a non-null one. Point the
  app at a different tenant -- or fix a mistyped tenant id, which is the same code path and a good deal more
  common -- and it built a credential whose AUTHORITY was the new tenant but whose bound ACCOUNT was still
  the old one. IsSignedIn and SignedInAccount went on reporting the previous identity although no sign-in to
  the new tenant had happened. Cancel the browser prompt that follows and the app carries on in that state:
  wrong-tenant requests, or opaque token failures, where it should simply have said "not signed in".

  It reaches further than Graph. The Exchange channel borrows SignedInAccount to CONNECT with, and since F6
  it keys its live session on that name -- so a stale one quietly defeats the check that exists to stop an
  admin's session being reused by the next admin.

  Nothing here touches the network or the real saved sign-in: AuthenticationRecord.Deserialize builds a
  record from JSON, and the tenant ids used are not GUIDs, so the record on disk can never match them.

  Run with:  pwsh -NoProfile -File ./app/build/test-graph-identity.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dll = Join-Path $repoRoot 'debug\UnifiedDirectoryManager.dll'
$azure = Join-Path $repoRoot 'debug\Azure.Identity.dll'
if (-not (Test-Path $dll)) { throw "Build first — could not find $dll" }
if (-not (Test-Path $azure)) { throw "Could not find $azure next to the app assembly." }
[System.Reflection.Assembly]::LoadFrom($azure) | Out-Null
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

$Graph = [UnifiedDirectoryManager.Services.GraphService]
$NonPublic = [System.Reflection.BindingFlags]'NonPublic,Instance'
$NonPublicStatic = [System.Reflection.BindingFlags]'NonPublic,Static'

$recordField = $Graph.GetField('_record', $NonPublic)
$belongsTo = $Graph.GetMethod('RecordBelongsTo', $NonPublicStatic)
if ($null -eq $recordField) { throw 'GraphService has no _record field — has it been refactored?' }
if ($null -eq $belongsTo)   { throw 'GraphService has no RecordBelongsTo — has it been refactored?' }

# What Azure.Identity persists after an interactive sign-in. Deserialize is the only public way to make one.
function New-Record([string]$user, [string]$tenant, [string]$client) {
    $json = '{"username":"' + $user + '","authority":"login.microsoftonline.com",' +
            '"homeAccountId":"home-' + $user + '","tenantId":"' + $tenant + '",' +
            '"clientId":"' + $client + '","version":"1.0"}'
    $ms = [System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes($json))
    return [Azure.Identity.AuthenticationRecord]::Deserialize($ms)
}

# A service configured for one tenant with a completed sign-in already on it.
# A scratch home for the saved sign-in. SignOut deletes that file, so a service built here must never
# be pointed at the real one.
$script:authSandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-graph-" + [guid]::NewGuid())
[void][System.IO.Directory]::CreateDirectory($script:authSandbox)

# The operator's own saved sign-in, recorded now and checked again at the very end. Nothing in this
# file may create, change or delete it. Asserted end-to-end rather than by reading the source, because
# a source regex can prove the constructor TAKES a directory and not that it USES one -- a mutation
# that ignored the argument deleted a sentinel at the real path with the whole suite still green.
$script:realRecordPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'UnifiedDirectoryManager\graph-auth.bin'
$script:realRecordBefore = Test-Path $script:realRecordPath
$script:realRecordSize = if ($script:realRecordBefore) { (Get-Item $script:realRecordPath).Length } else { -1 }

function New-SignedIn([string]$tenant, [string]$client, [string]$user) {
    $svc = $Graph::new($script:authSandbox)
    $svc.Configure($tenant, $client)
    $recordField.SetValue($svc, (New-Record $user $tenant $client))
    return $svc
}

Write-Host "`n== a saved sign-in belongs to one tenant and one app registration ==" -ForegroundColor Cyan
$rec = New-Record 'admin.a@contoso.com' 'tenant-A' 'client-1'
Check 'its own tenant and client match'  $true  $belongsTo.Invoke($null, @($rec, 'tenant-A', 'client-1'))
Check 'a different tenant does not'      $false $belongsTo.Invoke($null, @($rec, 'tenant-B', 'client-1'))
Check 'nor a different app registration' $false $belongsTo.Invoke($null, @($rec, 'tenant-A', 'client-2'))
# Tenant and client ids are GUIDs, which Entra renders in whatever case it likes.
Check 'casing is not a difference'       $true  $belongsTo.Invoke($null, @($rec, 'TENANT-A', 'CLIENT-1'))
Check 'no record belongs to anything'    $false $belongsTo.Invoke($null, @($null, 'tenant-A', 'client-1'))

Write-Host "`n== changing the tenant drops the signed-in identity ==" -ForegroundColor Cyan
$svc = New-SignedIn 'tenant-A' 'client-1' 'admin.a@contoso.com'
Check 'the setup is signed in'           $true 'admin.a@contoso.com'.Equals($svc.SignedInAccount)
Check 'and reports so'                   $true $svc.IsSignedIn

# THE BUG. Configure(tenantB) used to keep admin A's record: the credential's authority became B while its
# bound account stayed A's, and the app went on claiming A was signed in.
$svc.Configure('tenant-B', 'client-1')
Check 'a new tenant is not signed in'    $false $svc.IsSignedIn
Check 'and names nobody'                 $null  $svc.SignedInAccount
Check 'the record itself is dropped'     $null  $recordField.GetValue($svc)
Check 'but the app is still configured'  $true  $svc.IsConfigured

Write-Host "`n== so does changing the app registration ==" -ForegroundColor Cyan
# A different client id is a different application, with different consented scopes. The record encodes it.
$svc = New-SignedIn 'tenant-A' 'client-1' 'admin.a@contoso.com'
$svc.Configure('tenant-A', 'client-2')
Check 'a new client id is not signed in' $false $svc.IsSignedIn
Check 'and names nobody'                 $null  $svc.SignedInAccount

Write-Host "`n== but reconfiguring the SAME tenant keeps it ==" -ForegroundColor Cyan
# The negative control, and it is not a rare path: saving the Settings dialog re-runs Configure with the
# same identifiers, and so does every startup that has them stored. Dropping the sign-in there would send
# the operator back through the browser on each launch.
$svc = New-SignedIn 'tenant-A' 'client-1' 'admin.a@contoso.com'
$svc.Configure('tenant-A', 'client-1')
Check 'the sign-in survives'             $true  $svc.IsSignedIn
Check 'and still names the same admin'   'admin.a@contoso.com' $svc.SignedInAccount

# Entra hands the same GUID back in whatever case, and re-casing is not a change of tenant.
$svc = New-SignedIn 'tenant-A' 'client-1' 'admin.a@contoso.com'
$svc.Configure('TENANT-A', 'CLIENT-1')
Check 'nor does re-casing drop it'       $true  $svc.IsSignedIn

Write-Host "`n== clearing the tenant entirely ==" -ForegroundColor Cyan
# Blanking the identifiers is a change too, and must not leave the app claiming somebody is signed in.
$svc = New-SignedIn 'tenant-A' 'client-1' 'admin.a@contoso.com'
$svc.Configure('', '')
Check 'no tenant means not configured'   $false $svc.IsConfigured
Check 'and not signed in'                $false $svc.IsSignedIn

Write-Host "`n== signing out does not read the sign-in back off disk ==" -ForegroundColor Cyan
# SignOut deletes the saved record and rebuilds the credential. It used to rebuild by calling Configure,
# which RELOADS that record -- so if the delete failed (an antivirus scanner holding the file is the usual
# reason, and it is logged as a warning nobody reads) the operator was signed straight back in, on what may
# well be a shared workstation.
#
# The reload cannot be reproduced from here even now that the path has a seam: Configure() would need a
# real credential. So the ordering is asserted on the source. It is a genuine mutation check -- routing
# SignOut back through Configure flips it.
#
# This comment used to claim the suite would not touch the operator's real saved sign-in, two lines above
# a SignOut() that deleted it. The service below is built against a scratch directory; see New-SignedIn.
$svc = New-SignedIn 'tenant-A' 'client-1' 'admin.a@contoso.com'
$svc.SignOut()
Check 'signing out clears the record'    $null  $recordField.GetValue($svc)
Check 'and reports signed out'           $false $svc.IsSignedIn
Check 'while staying configured'         $true  $svc.IsConfigured

$src = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Services\GraphService.cs')

Write-Host "`n== and it signs out of a SANDBOX, not the operator's real record ==" -ForegroundColor Cyan
# This suite used to delete the real %APPDATA%\UnifiedDirectoryManager\graph-auth.bin every run,
# because SignOut deletes the saved record and the path was a fixed static with no seam. The symptom
# reached the operator as "I have to sign in again after every build" and nothing logged it, since a
# delete that succeeds has nothing to report.
Check 'the path is per instance'         $true  ($src -match 'private readonly string _authRecordPath')
Check '  not a fixed static'             $false ($src -match 'static readonly string AuthRecordPath')
Check '  and the ctor takes a directory' $true  ($src -match 'public GraphService\(string\? dataDirectory = null\)')
# Everything that touches the file must go through the instance field.
foreach ($op in 'Delete', 'OpenRead', 'Create') {
    Check "  File.$op uses the seam"     $true  ($src -match ('File\.' + $op + '\(_authRecordPath\)'))
}
# And the suite itself must be pointed away from the real location.
Check 'the suite uses a sandbox'         $true  ($script:authSandbox -like (Join-Path ([System.IO.Path]::GetTempPath()) 'udm-graph-*'))
$realRecord = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'UnifiedDirectoryManager\graph-auth.bin'
Check '  which is not the real path'     $false ($script:authSandbox -eq (Split-Path -Parent $realRecord))

Write-Host "`n== the seam is honoured, not merely declared ==" -ForegroundColor Cyan
# Proving SignOut deletes the record IN THE DIRECTORY IT WAS GIVEN. The source checks above cannot:
# they match a constructor signature and a field name, both of which survive a body that ignores the
# argument and goes on using the real location.
$seamDir = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-seam-" + [guid]::NewGuid())
[void][System.IO.Directory]::CreateDirectory($seamDir)
$seamFile = Join-Path $seamDir 'graph-auth.bin'
Set-Content -LiteralPath $seamFile -Value 'stand-in for a saved sign-in' -NoNewline
Check 'a record exists in the sandbox'   $true  (Test-Path $seamFile)
$seamSvc = $Graph::new($seamDir)
$seamSvc.Configure('tenant-A', 'client-1')
$seamSvc.SignOut()
Check '  and SignOut removes THAT one'   $false (Test-Path $seamFile)
Remove-Item -LiteralPath $seamDir -Recurse -Force -ErrorAction SilentlyContinue

$signOut = [regex]::Match($src, '(?s)public void SignOut\(\).*?\r?\n    \}').Value
Check 'SignOut was found'                $true  ($signOut.Length -gt 0)
Check 'it rebuilds the credential'       $true  ($signOut -match 'BuildCredential\(\)')
Check 'and does NOT call Configure'      $false ($signOut -match 'Configure\(')

Write-Host "`n== the record is only reused when it fits (mutation check) ==" -ForegroundColor Cyan
$configure = [regex]::Match($src, '(?s)public void Configure\(string tenantId, string clientId\).*?\r?\n    \}').Value
Check 'Configure was found'                  $true  ($configure.Length -gt 0)
Check 'it compares the incoming tenant'      $true  ($configure -match 'newTenant, _tenantId')
Check 'and the incoming client id'           $true  ($configure -match 'newClient, _clientId')
Check 'it drops the record on a change'      $true  ($configure -match '_record = null')
Check 'and never loads one blindly'          $false ($configure -match '_record \?\?= TryLoadAuthRecord\(\)')
Check 'loading one is tenant-scoped'         $true  ($configure -match '_record \?\?= LoadAuthRecordFor\(')

Write-Host "`n== the cloud sign-in is checked at startup ==" -ForegroundColor Cyan
# IsSignedIn only means a saved record exists, and a record OUTLIVES the token it was saved with: it
# stays on disk after the refresh token ages out, after consent is revoked, and after the account is
# disabled. Reporting from the record would say "signed in" for an account that stopped working months
# ago, and the operator would find out at the first cloud operation instead of at startup.
$Cloud = [UnifiedDirectoryManager.Services.CloudSignIn]
$CheckType = [UnifiedDirectoryManager.Services.CloudSignInCheck]
$State = [UnifiedDirectoryManager.Services.CloudSignInState]
function Warn($check) { $b = [object[]]::new(1); $b[0] = $check; return $Cloud.GetMethod('Warning').Invoke($null, $b) }
function CanSignIn($check) { $b = [object[]]::new(1); $b[0] = $check; return $Cloud.GetMethod('CanSignIn').Invoke($null, $b) }
function Mk($state, $account, $message) { return $CheckType::new($state, $account, $message) }

$ok = Mk $State::SignedIn 'jane@contoso.net' $null
Check 'a working sign-in says nothing'  '' (Warn $ok)
Check '  and offers no button'          $false (CanSignIn $ok)

# An operator doing only on-prem work has not got a problem. A warning they cannot act on and do not
# need is one they learn to ignore, which costs the warnings that matter.
$unconfigured = $CheckType::NotConfigured
Check 'an unconfigured tenant is silent' '' (Warn $unconfigured)
Check '  and offers no button'           $false (CanSignIn $unconfigured)

$out = Mk $State::NotSignedIn $null $null
Check 'signed out is reported'          $true ((Warn $out) -like '*Not signed in to Entra ID*')
Check '  with the consequence'          $true ((Warn $out) -like '*Exchange Online features are unavailable*')
Check '  and offers the button'         $true (CanSignIn $out)

$expired = Mk $State::Expired 'jane@contoso.net' $null
Check 'an expired sign-in is reported'  $true ((Warn $expired) -like '*has expired*')
Check '  naming whose it was'           $true ((Warn $expired) -like '*jane@contoso.net*')
Check '  and offers the button'         $true (CanSignIn $expired)
# The account is not always known; the sentence still has to read.
$expiredAnon = Mk $State::Expired $null $null
Check '  it reads without an account'   $true ((Warn $expiredAnon) -like '*saved Entra ID sign-in has expired*')
Check '  with no dangling "for"'        $false ((Warn $expiredAnon) -like '*for *has expired*')

# THE DISTINCTION THIS FILE EXISTS FOR. "Could not tell" is not "signed out". Telling someone to sign in
# again when the real problem is a dropped network sends them round a loop that cannot fix it.
$unknown = Mk $State::CheckFailed 'jane@contoso.net' 'No such host is known.'
Check 'an unreachable check says so'    $true ((Warn $unknown) -like '*Could not check*')
Check '  quoting why'                   $true ((Warn $unknown) -like '*No such host is known*')
Check '  never claiming signed out'     $false ((Warn $unknown) -like '*Not signed in*')
Check '  nor claiming expired'          $false ((Warn $unknown) -like '*expired*')
Check '  and offers NO button'          $false (CanSignIn $unknown)
Check 'null is treated as nothing'      '' (Warn $null)

# The wording switch names every state, and its catch-all THROWS rather than returning empty.
#
# It replaced a `default: return string.Empty`, which mutation testing showed had made the
# NotConfigured case inert -- deleting that case changed nothing, because both returned empty.
#
# Removing the catch-all altogether does not work, however appealing: a C# enum is not a closed set, so
# (CloudSignInState)5 is legal and the compiler demands an arm for it. That is CS8524, and it reached
# CI because a local INCREMENTAL build had reported success for a compile that never ran. So the guard
# against forgetting a state is this suite, not the compiler -- which is why it checks every member by
# name, and checks that the catch-all cannot quietly swallow one.
$cloudSrc = Get-Content -Raw (Join-Path (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager') 'Services\CloudSignIn.cs')
$switchBlock = [regex]::Match($cloudSrc, '(?s)return check\.State switch.*?\n        \};').Value
Check 'the wording switch was found'    $true ($switchBlock.Length -gt 0)
Check '  it has a catch-all'            $true  ($switchBlock -match '_\s*=>')
Check '  which throws'                  $true  ($switchBlock -match '_\s*=>\s*throw')
# The failure that matters is a state nobody wrote a sentence for being shown as no warning at all.
Check '  and never returns empty'       $false ($switchBlock -match '_\s*=>\s*string\.Empty')
Check '  with no default label'         $false ($switchBlock -match 'default\s*:')
# Every state named, so the compiler has something to check against.
foreach ($state in 'SignedIn', 'NotConfigured', 'NotSignedIn', 'Expired', 'CheckFailed') {
    Check "  it handles $state" $true ($switchBlock -match ('CloudSignInState\.' + $state))
}

$srcDir = Join-Path $repoRoot 'app\src\UnifiedDirectoryManager'
Write-Host "`n== the check never opens a browser ==" -ForegroundColor Cyan
# The credential used for real work falls back to INTERACTIVE when silent acquisition fails, which is
# right when an operator asked for something and wrong at startup -- it would put a sign-in window in
# front of someone who only wanted to look up an on-prem user.
$graphSrc = Get-Content -Raw (Join-Path $srcDir 'Services\GraphService.cs')
$checkBlock = [regex]::Match($graphSrc, '(?s)public async Task<CloudSignInCheck> CheckSignInAsync.*?\n    \}').Value
Check 'the check was found'             $true ($checkBlock.Length -gt 0)
Check '  it disables the prompt'        $true ($checkBlock -match 'DisableAutomaticAuthentication = true')
Check '  and reads the expiry case'     $true ($checkBlock -match 'catch \(AuthenticationRequiredException\)')
# Everything else is "could not tell", not "signed out".
Check '  anything else is CheckFailed'  $true ($checkBlock -match 'CloudSignInState\.CheckFailed')
Check '  and never authenticates'       $false ($checkBlock -match 'AuthenticateAsync')

Write-Host "`n== and it runs where it should ==" -ForegroundColor Cyan
$mainSrc = Get-Content -Raw (Join-Path $srcDir 'ViewModels\MainViewModel.cs')
$startup = [regex]::Match($mainSrc, '(?s)public async Task StartupAsync\(\).*?\n    \}').Value
Check 'startup checks the sign-in'      $true ($startup -match 'CheckCloudSignInAsync\(\)')
# After the on-prem attempt: that is what most sessions are waiting on.
Check '  after the on-prem connect'     $true ($startup.IndexOf('CheckCloudSignInAsync') -gt $startup.IndexOf('TryAutoConnectAsync'))
# Signing in or out is the whole point of the Cloud page, so closing Settings has to re-ask.
$settingsBlock = [regex]::Match($mainSrc, '(?s)private void OpenSettings\(\).*?\n    \}').Value
Check 'closing Settings re-checks'      $true ($settingsBlock -match 'CheckCloudSignInAsync')
# A failed check must never be the thing that breaks startup.
$checkMethod = [regex]::Match($mainSrc, '(?s)public async Task CheckCloudSignInAsync\(\).*?\n    \}').Value
Check 'the check cannot throw'          $true ($checkMethod -match 'catch \(Exception')

$mainXamlSrc = Get-Content -Raw (Join-Path $srcDir 'Views\MainWindow.xaml')
Check 'the bar is in the window'        $true ($mainXamlSrc -match 'CloudWarning, Converter=\{StaticResource NonEmptyToVis\}')
Check '  with a Sign in button'         $true ($mainXamlSrc -match 'SignInToCloudCommand')
# Hidden when signing in cannot help, which is the CheckFailed case.
Check '  that hides when it cannot help' $true ($mainXamlSrc -match 'CanSignInToCloud, Converter=\{StaticResource BoolToVis\}')

Write-Host "`n== and the operator's own saved sign-in was never touched ==" -ForegroundColor Cyan
# The backstop. Whatever else changes in this file, running it must leave the real record exactly as it
# was found -- present or absent, same size. This suite used to delete it on every run.
$realAfter = Test-Path $script:realRecordPath
$sizeAfter = if ($realAfter) { (Get-Item $script:realRecordPath).Length } else { -1 }
Check 'still exactly as it was found'    $script:realRecordBefore $realAfter
Check '  and the same size'              $script:realRecordSize   $sizeAfter

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
