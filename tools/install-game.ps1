# Builds the player runtime and installs it into a BepInEx game folder.
[CmdletBinding()]
param(
    [string]$BepInExDir = "D:\SteamLibrary\steamapps\common\Casualties Unknown Demo\BepInEx",
    [ValidateSet("Debug", "Release")][string]$Configuration = "Release",
    [string]$CMakeGenerator = "Visual Studio 17 2022",
    [string]$CMakeToolset = "v142"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$BepInExDir = (Resolve-Path $BepInExDir).Path
$gameDir = Split-Path $BepInExDir -Parent

foreach ($required in @(
    (Join-Path $BepInExDir "core\BepInEx.dll"),
    (Join-Path $gameDir "CasualtiesUnknown_Data\Managed\Assembly-CSharp.dll"),
    (Join-Path $root "src\CasualtyVR.Shaders\Bundles\casualtyvr_shaders")
)) {
    if (-not (Test-Path $required -PathType Leaf)) {
        throw "Required game or build file not found: $required"
    }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw "The .NET SDK is required. Install it, then rerun this script." }
$sdks = & $dotnet.Source --list-sdks
if ($LASTEXITCODE -ne 0 -or -not $sdks) {
    throw "No .NET SDK is installed. Install the .NET SDK, then rerun this script."
}

$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        foreach ($installation in (& $vswhere -all -products "*" -property installationPath)) {
            $candidate = Join-Path $installation "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
            if (Test-Path $candidate) {
                $cmake = $candidate
                break
            }
        }
    }
}
if (-not $cmake) { throw "CMake is required. Add it to PATH or install it with Visual Studio." }

$coreProject = Join-Path $root "src\CasualtyVR.Core\CasualtyVR.Core.csproj"
& $dotnet.Source build $coreProject --configuration $Configuration --nologo -v quiet "-p:GameDir=$gameDir"
if ($LASTEXITCODE -ne 0) { throw "CasualtyVR.Core build failed." }

$bridgeSource = Join-Path $root "src\CasualtyVR.XrBridge"
$bridgeBuild = Join-Path $root "Working\build\CasualtyVR.XrBridge"
New-Item -ItemType Directory -Force $bridgeBuild | Out-Null
if (-not (Test-Path (Join-Path $bridgeBuild "CMakeCache.txt"))) {
    & $cmake -S $bridgeSource -B $bridgeBuild -G $CMakeGenerator -A x64 -T $CMakeToolset
    if ($LASTEXITCODE -ne 0) { throw "OpenXR bridge configure failed." }
}
& $cmake --build $bridgeBuild --config Release -- /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "OpenXR bridge build failed." }

$pluginDir = Join-Path $BepInExDir "plugins\CasualtyVR"
$nativeDir = Join-Path $pluginDir "native"
$artifacts = @(
    @{ Path = (Join-Path $root "src\CasualtyVR.Core\bin\$Configuration\CasualtyVR.Core.dll"); Destination = $pluginDir },
    @{ Path = (Join-Path $bridgeBuild "Release\CasualtyVR.XrBridge.dll"); Destination = $nativeDir },
    @{ Path = (Join-Path $bridgeBuild "Release\openxr_loader.dll"); Destination = $nativeDir },
    @{ Path = (Join-Path $root "src\CasualtyVR.Shaders\Bundles\casualtyvr_shaders"); Destination = $pluginDir }
)
foreach ($artifact in $artifacts) {
    if (-not (Test-Path $artifact.Path -PathType Leaf)) {
        throw "Build output not found: $($artifact.Path)"
    }
}

foreach ($destination in @($pluginDir, $nativeDir)) {
    New-Item -ItemType Directory -Force $destination | Out-Null
}
foreach ($artifact in $artifacts) {
    Copy-Item $artifact.Path $artifact.Destination -Force
    Write-Host "Installed $([IO.Path]::GetFileName($artifact.Path)) -> $($artifact.Destination)"
}
