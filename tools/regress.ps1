# The headless regression in one go: every suite in order, a summary table with each suite's time, and the dev config
# restored at the end (the suites restore their own settings, but every launch lets the plugin rewrite the file).
# Dev copy only; never run two of these (or a suite and this) at once: they share the game, xr-sim and the logs.
#
#   tools/regress.ps1           everything (about 20 game launches)
#   tools/regress.ps1 -Quick    a pass for iterating: fp-smoke -Quick, the lifecycle input session, the 3D settings entry
#                               session and xr-tape (5 launches)
#   tools/regress.ps1 -Skip vr-menu,fp-comfort
#
# Exit code = the number of suites that failed. Each suite's own output is in Working/regress/<suite>.txt.
[CmdletBinding()]
param([switch]$Quick, [string[]]$Skip = @())
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$cfg = Join-Path $root "Working\game-dev\BepInEx\config\casualtyvr.core.cfg"
$out = Join-Path $root "Working\regress"
New-Item -ItemType Directory -Force $out | Out-Null
$cfgSaved = Join-Path $out "casualtyvr.core.cfg.before"
Copy-Item $cfg $cfgSaved -Force

# Name, script, named arguments (a hashtable: switches do not bind from a string array), launch-dev's game arguments,
# and how to read the result from its output when the exit code does not carry it.
$suites = if ($Quick) {
    @(
        @{ Name = "fp-smoke";     Script = "fp-smoke.ps1";     Args = @{ Quick = $true } },
        @{ Name = "vr-lifecycle"; Script = "vr-lifecycle.ps1"; Args = @{ Only = @("input") } },
        @{ Name = "vr-menu";      Script = "vr-menu.ps1";      Args = @{ Only = @("pause") } },
        @{ Name = "xr-tape";      Script = "launch-dev.ps1";   Args = @{ Windowed = $true; XrSim = $true; XrTape = $true }; Rest = @("--cvr-vr", "--cvr-autostart", "--cvr-seed", "1234", "--cvr-quit"); Pass = "xrtape_check exit 0" }
    )
} else {
    @(
        @{ Name = "fp-smoke";     Script = "fp-smoke.ps1";     Args = @{} },
        @{ Name = "vr-menu";      Script = "vr-menu.ps1";      Args = @{} },
        @{ Name = "vr-lifecycle"; Script = "vr-lifecycle.ps1"; Args = @{} },
        @{ Name = "fp-comfort";   Script = "fp-comfort.ps1";   Args = @{} },
        @{ Name = "motion-takes"; Script = "motion-takes.ps1"; Args = @{} },
        @{ Name = "xr-tape";      Script = "launch-dev.ps1";   Args = @{ Windowed = $true; XrSim = $true; XrTape = $true }; Rest = @("--cvr-vr", "--cvr-autostart", "--cvr-seed", "1234", "--cvr-quit"); Pass = "xrtape_check exit 0" }
    )
}

$rows = @()
$startedAt = Get-Date
try {
    foreach ($s in $suites) {
        if ($Skip -contains $s.Name) { continue }
        $named = $s.Args
        $rest = @(if ($s.Rest) { $s.Rest })   # @(): an empty array returned from an if would arrive as $null
        Write-Host "=== $($s.Name) $(($named.GetEnumerator() | ForEach-Object { "-$($_.Key) $($_.Value)" }) -join ' ') $($rest -join ' ')"
        $t0 = Get-Date
        $script = Join-Path $root "tools\$($s.Script)"
        $global:LASTEXITCODE = 0
        $text = & $script @named @rest *>&1 | Out-String -Width 250
        $code = $LASTEXITCODE
        $text | Set-Content (Join-Path $out "$($s.Name).txt") -Encoding utf8
        # The suite's own summary line ("22/22 passed in 125 s", "0 failure(s) ...", "19 passed, 0 failed ...").
        $summary = ($text -split "`r?`n" | Where-Object { $_ -match "\d+/\d+ passed|failure\(s\)|\d+ passed, \d+ failed" } | Select-Object -Last 1)
        $ok = if ($s.Pass) { $text -match [regex]::Escape($s.Pass) } else { $code -eq 0 }
        $rows += [pscustomobject]@{ Suite = $s.Name; Result = $(if ($ok) { "PASS" } else { "FAIL" }); Seconds = [int]((Get-Date) - $t0).TotalSeconds; Summary = "$summary".Trim() }
        $failLines = $text -split "`r?`n" | Where-Object { $_ -match "\sFAIL\s" }
        foreach ($f in $failLines) { Write-Host "  $($f.Trim())" }
    }
}
finally {
    Get-Process CasualtiesUnknown -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*game-dev*" } | Stop-Process -Force
    Copy-Item $cfgSaved $cfg -Force
}
$rows | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
$failed = @($rows | Where-Object { $_.Result -eq "FAIL" }).Count
Write-Host "$($rows.Count - $failed)/$($rows.Count) suites passed in $([int]((Get-Date) - $startedAt).TotalSeconds) s (output: $out)"
exit $failed
