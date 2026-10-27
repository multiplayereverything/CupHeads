# Mutation checks: each run breaks one sync path (both lab copies drop that packet type on send) and lists the checks
# that must then fail. A mutant is "caught" when every one of them fails. The "resilience" entries are the opposite:
# a path with a backup must still pass. Results: artifacts/tests/mutants-summary.json.
# Usage: mutants.ps1 [-Only name,name]
param([string[]]$Only = @())
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$mutants = [ordered]@{
    'input'    = @{ Drop = 'InputFrame'; MustFail = @('map.guest-cup-moves', 'level.guest-cup-moves') }
    'state'    = @{ Drop = 'PlayerState'; MustFail = @('level.host-cup-moves') }
    'boss'     = @{ Drop = 'EnemyState,StateHash'; MustFail = @('boss-hp-sync') }
    'scenes'   = @{ Drop = 'SceneChange,MenuSceneChange,SceneRequest,LevelSync'; MustFail = @('level-transition', 'back-to-map') }
    'health'   = @{ Drop = 'DamageEvent,PlayerStatus'; MustFail = @('guest-hits-and-death') }
    'revive'   = @{ Drop = 'ReviveRequest,ReviveGrant'; MustFail = @('revive', 'parry-revive') }
    # Lost hits: the owner's status stream must still bring the other copy down (ParticipantStatusTracker.ReconcileRemoteBodies).
    'lost-hits' = @{ Drop = 'DamageEvent'; MustPass = @('guest-hits-and-death', 'guest-second-death') }
}
$summary = @()
foreach ($name in $mutants.Keys) {
    if ($Only.Count -gt 0 -and $Only -notcontains $name) { continue }
    $m = $mutants[$name]
    $spec = "drop:$($m.Drop)"
    Write-Output "=== $name ($spec)"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'run-lab.ps1') -Mutate $spec | Out-Null
    $tag = 'mut-' + ($spec -replace '[^A-Za-z0-9]+', '-')
    $res = Get-Content (Join-Path $root "artifacts\tests\$tag\results.json") -Raw | ConvertFrom-Json
    $byName = @{}; foreach ($r in $res.results) { $byName[$r.check] = $r }
    $ok = $true; $notes = @()
    foreach ($c in @($m.MustFail)) { if ($c) { $hit = $byName[$c] -and -not $byName[$c].pass; $ok = $ok -and $hit; $notes += "$c " + $(if ($hit) { 'failed (caught)' } else { 'PASSED (missed)' }) } }
    foreach ($c in @($m.MustPass)) { if ($c) { $hit = $byName[$c] -and $byName[$c].pass; $ok = $ok -and $hit; $notes += "$c " + $(if ($hit) { 'passed (backup works)' } else { 'FAILED' }) } }
    Write-Output ("{0}: {1} - {2}" -f $name, $(if ($ok) { 'OK' } else { 'NOT OK' }), ($notes -join '; '))
    $summary += [pscustomobject]@{ mutant = $name; drop = $m.Drop; ok = $ok; notes = $notes; passed = $res.passed; total = $res.total }
}
$summary | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'artifacts\tests\mutants-summary.json')
if ($summary | Where-Object { -not $_.ok }) { exit 1 }
