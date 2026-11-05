# Builds src\CasualtyVR.Shaders\Bundles\casualtyvr_shaders: CasualtyVR's shader variants (fog and desktop flashlight)
# of URP 14.0.12 (the game's
# URP), compiled for D3D11 / Windows x64 by Unity 2022.3.62f3 in batch mode (docs/SHADERS.md). The bundle is
# committed next to its sources, so normal builds (deploy-dev, package-release) do not need Unity; run this after
# changing a shader.
[CmdletBinding()]
param([string]$Unity = "F:\Unity\Hub\Editor\2022.3.62f3\Editor\Unity.exe")
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\CasualtyVR.Shaders" | Resolve-Path
$log = Join-Path $project "build.log"
$p = Start-Process -FilePath $Unity -ArgumentList @("-batchmode", "-quit", "-projectPath", $project, "-executeMethod", "BuildCasualtyVRBundle.Build", "-logFile", $log) -PassThru -Wait
$lines = Select-String -Path $log -Pattern "CasualtyVR shader|Shader error|bundle built|bundle FAILED" | ForEach-Object { $_.Line }
$lines
if ($p.ExitCode -ne 0 -or ($lines -match "Shader error|FAILED|errors [1-9]")) { throw "Shader build failed (see $log)" }
Get-Item (Join-Path $project "Bundles\casualtyvr_shaders") | Select-Object FullName, Length
