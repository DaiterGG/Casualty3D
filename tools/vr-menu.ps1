# 3D settings menu checks (docs/BUILD_PLAN.md Phase 1). Dev copy only (tools/launch-dev.ps1);
# restores the dev config afterwards.
#
#   tools/vr-menu.ps1                 all scenarios; exit code = number of failures
#   tools/vr-menu.ps1 -Only layout
#
# Scenarios (each a fresh game, seed 1234):
#   layout   each remaining settings tab, with and without "More settings"; an xr-sim capture per tab
#   pause    the pause menu (xr-sim menu button) has the round 3D button on the game's button arc
#   zoom     game menus remain zoomed on their VR panels
[CmdletBinding()]
# -Size 2560x1440: run the game window at that size instead of -Windowed (menus scale with it).
param([string[]]$Only = @(), [string]$Size = "")
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$d = "$env:LOCALAPPDATA\xr-sim\casualtyvr"
$t = Join-Path $root "tools\xr-sim\tools"
$bepinexLog = Join-Path $root "Working\game-dev\BepInEx\LogOutput.log"
$cfg = Join-Path $root "Working\game-dev\BepInEx\config\casualtyvr.core.cfg"
$cfgBackup = Join-Path $env:TEMP "casualtyvr.core.cfg.vr-menu"
$log = $bepinexLog
$results = New-Object System.Collections.Generic.List[object]

function C([string[]]$c) { & "$t\xrsim-cmd.ps1" -Dir $d -Quiet @c | Out-Null }
function Has([string]$pattern) { (Test-Path $log) -and (Select-String -Path $log -Pattern $pattern -Quiet) }
function Wait-For([string]$pattern, [int]$seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (Has $pattern) { return $true }; Start-Sleep -Milliseconds 400 }
    return $false
}
function Stop-Game { Get-Process CasualtiesUnknown -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*game-dev*" } | Stop-Process -Force; Start-Sleep 2 }
function Start-Game([string[]]$extra) {
    Stop-Game
    Copy-Item $cfgBackup $cfg -Force   # each scenario starts from the dev config
    Remove-Item $bepinexLog -ErrorAction SilentlyContinue
    $script:log = $bepinexLog
    $started = Get-Date
    if ($Size) {
        $w, $h = $Size -split "x"
        # The dev config keeps the window on a 1920x1080 monitor (Window.PreferredDisplay): not for this run.
        $c = [IO.File]::ReadAllText($cfg)
        [IO.File]::WriteAllText($cfg, [regex]::Replace($c, "(?m)^PreferredDisplay = .*$", "PreferredDisplay = "))
        & (Join-Path $root "tools\launch-dev.ps1") -XrSim -NoWait (@("-screen-fullscreen", "0", "-screen-width", $w, "-screen-height", $h, "--cvr-vr", "--cvr-autostart", "--cvr-seed", "1234") + $extra) | Out-Null
    }
    else { & (Join-Path $root "tools\launch-dev.ps1") -Windowed -XrSim -NoWait (@("--cvr-vr", "--cvr-autostart", "--cvr-seed", "1234") + $extra) | Out-Null }
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 300
        $newest = Get-ChildItem (Join-Path $root "Working\game-dev\CasualtyVR_Logs\Player-*.log") | Where-Object { $_.CreationTime -ge $started.AddSeconds(-2) } | Sort-Object CreationTime | Select-Object -Last 1
    } while ($null -eq $newest -and (Get-Date) -lt $deadline)
    if ($newest) { $script:log = $newest.FullName }
    $ready = Wait-For "perf: ([5-9]\d)\.\d fps.* voxels [1-9]" 180
    C @("input clear", "head rot 0 0 0")
    return $ready
}
function Check([string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Scenario = $name; Result = $(if ($ok) { "PASS" } else { "FAIL" }); Detail = $detail })
}
function Check-NoExceptions([string]$name) {
    $lines = @(Select-String -Path $log -Pattern "Exception")
    Check "$name`: no exceptions" ($lines.Count -eq 0) ("{0} lines{1}" -f $lines.Count, $(if ($lines.Count) { ": " + $lines[0].Line } else { "" }))
}
function Want([string]$s) { $Only.Count -eq 0 -or $Only -contains $s }
Copy-Item $cfg $cfgBackup -Force
try {
    if (Want "layout") {
        $out = Join-Path $root "Working\menu-phase3"
        New-Item -ItemType Directory -Force $out | Out-Null
        foreach ($more in @($false, $true)) {
            $extra = @("--cvr-dbg-settings", "all") + $(if ($more) { @("--cvr-dbg-settings-more") } else { @() })
            $ready = Start-Game $extra
            Check "layout$(if ($more) { ' more' }): game ready" $ready ""
            # The menu opens 8 s into the level and shows each of the 3 tabs for 3 s, round and round: capture each tab
            # about a second after it appears (a line already there when this loop starts may be nearly 3 s old).
            $seen = @{}
            $count = @(Select-String -Path $log -Pattern "VR settings: tab '([^']+)'").Count
            $deadline = (Get-Date).AddSeconds(75)
            while ((Get-Date) -lt $deadline -and $seen.Count -lt 3) {
                $lines = @(Select-String -Path $log -Pattern "VR settings: tab '([^']+)'")
                if ($lines.Count -gt $count) {
                    $count = $lines.Count
                    $tab = $lines[-1].Matches[0].Groups[1].Value
                    if (-not $seen.ContainsKey($tab)) {
                        Start-Sleep -Milliseconds 1000
                        $name = ($tab -replace '[^A-Za-z0-9]', '') + $(if ($more) { "-more" } else { "" })
                        & "$t\xrsim-shot.ps1" -Dir $d -Out (Join-Path $out $name) -Quiet | Out-Null
                        $seen[$tab] = $true
                    }
                }
                Start-Sleep -Milliseconds 200
            }
            Start-Sleep 2
            $checks = @(Select-String -Path $log -Pattern "VR settings check: (.*)" | ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique)
            Check "layout$(if ($more) { ' more' }): all tabs checked" ($checks.Count -ge 3) ("{0} check lines" -f $checks.Count)
            foreach ($c in $checks) { Check ("layout: " + ($c -replace ':.*', '')) ($c -notmatch "FAIL") $c }
            Check-NoExceptions "layout$(if ($more) { ' more' })"
        }
    }

    if (Want "zoom") {
        # V7: the game's own menus on their own panel, at the settings panel's pixel size (B craft, Y health, X inventory).
        $ready = Start-Game @()
        Check "zoom: game ready" $ready ""
        C @("btn b press 150"); Start-Sleep 2
        Check "zoom: the crafting panel is zoomed" (Wait-For "UI: game menu zoom on: crafting" 5) ((Select-String -Path $log -Pattern "game menu zoom on: crafting" | Select-Object -Last 1).Line)
        & "$t\xrsim-shot.ps1" -Dir $d -Out (Join-Path $root "Working\menu-phase3\zoom-crafting") -Quiet | Out-Null
        C @("btn b press 150"); Start-Sleep 1.5
        Check "zoom: closing it ends the zoom" (Wait-For "UI: game menu zoom off" 5) "game menu zoom off"
        C @("btn y press 150"); Start-Sleep 2
        Check "zoom: the wound view is zoomed" (Wait-For "UI: game menu zoom on: wound view" 5) ((Select-String -Path $log -Pattern "game menu zoom on: wound view" | Select-Object -Last 1).Line)
        & "$t\xrsim-shot.ps1" -Dir $d -Out (Join-Path $root "Working\menu-phase3\zoom-woundview") -Quiet | Out-Null
        C @("btn y press 150"); Start-Sleep 1
        Check-NoExceptions "zoom"
        $ready = Start-Game @("--cvr-dbg-fp", "--cvr-dbg-cave")
        Check "zoom fp: game ready" $ready ""
        Start-Sleep 4
        C @("btn x press 150"); Start-Sleep 2
        Check "zoom fp: the inventory is zoomed in first person" (Wait-For "UI: game menu zoom on: inventory" 5) ((Select-String -Path $log -Pattern "game menu zoom on: inventory" | Select-Object -Last 1).Line)
        & "$t\xrsim-shot.ps1" -Dir $d -Out (Join-Path $root "Working\menu-phase3\zoom-inventory-fp") -Quiet | Out-Null
        Check-NoExceptions "zoom fp"
    }

    if (Want "pause") {
        $ready = Start-Game @()
        Check "pause: game ready" $ready ""
        C @("btn menu press 150"); Start-Sleep 2.5
        Check "pause: round 3D button on the arc" (Has "3D settings: round button added to the pause menu") ((Select-String -Path $log -Pattern "round button added" | Select-Object -Last 1).Line)
        & "$t\xrsim-shot.ps1" -Dir $d -Out (Join-Path $root "Working\menu-phase3\pause") -Quiet | Out-Null
        Check-NoExceptions "pause"
    }

}
finally {
    Stop-Game
    Copy-Item $cfgBackup $cfg -Force
}
$results | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host
$fails = @($results | Where-Object { $_.Result -eq "FAIL" }).Count
Write-Host "$fails failure(s). Log: $log"
exit $fails
