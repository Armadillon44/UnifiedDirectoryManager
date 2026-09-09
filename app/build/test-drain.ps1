<#
.SYNOPSIS
  Behavioural tests for PageDrain — the paging loop behind every "read the whole list" call.

.DESCRIPTION
  These drive the real loop with an in-memory fetch function: no Graph, no network, no fakes of the SDK.
  That is the whole reason the loop was extracted. Inline in GraphService it could only be asserted about
  with a regex over the source, which proves the code was written but not that it behaves.

  What is tested here is what is OURS to get wrong: does it stop, does it return every page, does it drop
  the last one, what does it do when the pages never end. Whether the Graph SDK deserialises a continuation
  link correctly is Microsoft's contract, not this loop's, and is not tested here.

  Run with:  pwsh -NoProfile -File ./app/build/test-drain.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path (Split-Path -Parent $root) 'debug\UnifiedDirectoryManager.dll'
if (-not (Test-Path $dll)) { throw "Build first — could not find $dll" }
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

# PageDrain is internal and generic, so it is reached by reflection and closed over string.
$asm = [System.Reflection.Assembly]::LoadFrom($dll)
$drainType = $asm.GetType('UnifiedDirectoryManager.Services.PageDrain')
Check 'PageDrain is present' $true ($null -ne $drainType)
$pageType = $drainType.GetNestedType('Page`1', [System.Reflection.BindingFlags]'NonPublic,Public').MakeGenericType([string])
$drain = $drainType.GetMethod('DrainAsync', [System.Reflection.BindingFlags]'NonPublic,Static').MakeGenericMethod([string])

# The delegate and its Task must be the exact closed generic types, or the reflected Invoke rejects them.
$taskOfPage = [System.Threading.Tasks.Task`1].MakeGenericType($pageType)
$fetchType  = [System.Func`3].MakeGenericType([string], [System.Threading.CancellationToken], $taskOfPage)
$tcsType    = [System.Threading.Tasks.TaskCompletionSource`1].MakeGenericType($pageType)

# Wraps a page value in a correctly-typed completed Task.
function CompletedPage($page) {
    $tcs = [System.Activator]::CreateInstance($tcsType)
    $tcs.SetResult($page)
    return $tcs.Task
}
function MakePage([string[]]$items, [string]$link) {
    [System.Activator]::CreateInstance($pageType, @([System.Collections.Generic.IEnumerable[string]]$items, $link))
}

# Builds the pages a run will serve, then hands back a fetch delegate over them plus a call log.
function NewSource([object[]]$pages) {
    $calls = New-Object System.Collections.Generic.List[string]
    $body = {
        param($next, $ct)
        $calls.Add($(if ([string]::IsNullOrEmpty($next)) { '<first>' } else { $next }))
        $i = if ([string]::IsNullOrEmpty($next)) { 0 } else { [int]($next -replace '\D', '') }
        $spec = $pages[$i]
        CompletedPage (MakePage ([string[]]$spec.Items) ([string]$spec.Next))
    }.GetNewClosure()
    return @{ Fetch = ($body -as $fetchType); Calls = $calls }
}
function Drain($src, [int]$maxPages = 50) {
    $describe = [System.Func[int, string]] { param($read) "read $read so far" }
    $task = $drain.Invoke($null, @($src.Fetch, $maxPages, $describe, [System.Threading.CancellationToken]::None))
    return $task.GetAwaiter().GetResult()
}

Write-Host "`n== it returns every page, in order ==" -ForegroundColor Cyan
# The defect this loop exists to prevent: stopping after page one and presenting it as the whole answer.
$src = NewSource @(
    @{ Items = @('a', 'b'); Next = 'link1' },
    @{ Items = @('c', 'd'); Next = 'link2' },
    @{ Items = @('e');      Next = $null   }
)
$got = Drain $src
Check 'every item across three pages' 5 $got.Count
Check 'in the order served'           'a,b,c,d,e' ($got -join ',')
Check 'and it fetched three times'    3 $src.Calls.Count
# The first call must ask for no continuation; later calls must pass the link the PREVIOUS page carried.
Check 'the first fetch has no link'   '<first>' $src.Calls[0]
Check 'the second follows page one'   'link1'   $src.Calls[1]
Check 'the third follows page two'    'link2'   $src.Calls[2]

Write-Host "`n== the last page is not dropped ==" -ForegroundColor Cyan
# An off-by-one that returns on seeing the link, rather than after consuming the page, loses the final page.
$src = NewSource @(
    @{ Items = @('first'); Next = 'link1' },
    @{ Items = @('last');  Next = $null   }
)
$got = Drain $src
Check 'both pages are present' 'first,last' ($got -join ',')

Write-Host "`n== a single page needs exactly one fetch ==" -ForegroundColor Cyan
$src = NewSource @( @{ Items = @('only'); Next = $null } )
$got = Drain $src
Check 'the item is returned'   'only' ($got -join ',')
Check 'with one call'          1      $src.Calls.Count

Write-Host "`n== an empty result is empty, not an error ==" -ForegroundColor Cyan
$src = NewSource @( @{ Items = @(); Next = $null } )
Check 'no items'    0 (Drain $src).Count
# A page with no items but a continuation still has to be followed — an empty page is not the end.
$src = NewSource @(
    @{ Items = @();      Next = 'link1' },
    @{ Items = @('late'); Next = $null  }
)
$got = Drain $src
Check 'an empty first page is still followed' 'late' ($got -join ',')

Write-Host "`n== it refuses to return a partial list ==" -ForegroundColor Cyan
# Reaching the page cap must THROW. Returning what was read so far would be the original bug wearing a
# different hat: a short list presented as complete.
$src = NewSource @(
    @{ Items = @('p1'); Next = 'link1' },
    @{ Items = @('p2'); Next = 'link2' },
    @{ Items = @('p3'); Next = 'link3' },
    @{ Items = @('p4'); Next = $null   }
)
$threw = $null
try { Drain $src 2 | Out-Null } catch { $threw = $_.Exception.GetBaseException() }
Check 'hitting the cap throws'            $true ($null -ne $threw)
Check 'and says it is refusing'           $true ($threw.Message -like '*refusing to return a partial list*')
# The message must report what was actually read, not a figure derived from page size.
Check 'and reports what it had read'      $true ($threw.Message -like '*read 2 so far*')

Write-Host "`n== a service that never advances cannot spin forever ==" -ForegroundColor Cyan
# A continuation link that repeats itself would loop until the cap. Catching it by identity fails fast and
# says why, rather than burning the whole page budget on the same page.
$stuckCalls = New-Object System.Collections.Generic.List[string]
$stuckBody = {
    param($next, $ct)
    $stuckCalls.Add('call')
    CompletedPage (MakePage ([string[]]@('x')) 'same-link')
}.GetNewClosure()
$stuck = @{ Fetch = ($stuckBody -as $fetchType); Calls = $stuckCalls }
$threw = $null
try { Drain $stuck 50 | Out-Null } catch { $threw = $_.Exception.GetBaseException() }
Check 'a repeated link throws'        $true ($null -ne $threw)
Check 'and names the reason'          $true ($threw.Message -like '*repeated the same continuation link*')
# It must give up immediately, not grind through all 50 allowed pages first.
Check 'without exhausting the budget' $true ($stuckCalls.Count -le 3)

Write-Host "`n== guards on its own arguments ==" -ForegroundColor Cyan
$threw = $null
try { Drain (NewSource @( @{ Items = @('a'); Next = $null } )) 0 | Out-Null } catch { $threw = $_.Exception.GetBaseException() }
Check 'a zero page budget is rejected' 'ArgumentOutOfRangeException' $(if ($threw) { $threw.GetType().Name })

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
