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
function New-SignedIn([string]$tenant, [string]$client, [string]$user) {
    $svc = $Graph::new()
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
# The reload cannot be reproduced from here: the path is a fixed %APPDATA% location with no seam, and this
# suite will not write to the operator's real saved sign-in. So the ordering is asserted on the source. It
# is a genuine mutation check -- routing SignOut back through Configure flips it.
$svc = New-SignedIn 'tenant-A' 'client-1' 'admin.a@contoso.com'
$svc.SignOut()
Check 'signing out clears the record'    $null  $recordField.GetValue($svc)
Check 'and reports signed out'           $false $svc.IsSignedIn
Check 'while staying configured'         $true  $svc.IsConfigured

$src = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Services\GraphService.cs')
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

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
