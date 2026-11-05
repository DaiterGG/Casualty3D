# SHADERS — custom shaders through a Unity-built asset bundle (2026-11-05)

## Pipeline

- **Project:** `src/CasualtyVR.Shaders` (Unity 2022.3.62f3).
  - Its `Packages/manifest.json` pins `com.unity.render-pipelines.universal` **14.0.12**, the game's URP. The version
    string is in `Unity.RenderPipeline.Universal.ShaderLibrary.dll`.
  - The editor runs headless: batch mode works with the installed entitlement licence.
- **Build:** `tools/build-shaders.ps1` runs `BuildCasualtyVRBundle.Build` (Assets/Editor), which builds
  `Bundles/casualtyvr_shaders` for D3D11 on Windows x64 (about 17 KB). The script fails on any shader error.
- **Deploy and package:** the built bundle is kept in the repo, so `deploy-dev.ps1` and `package-release.ps1` only
  copy it to `BepInEx/plugins/CasualtyVR/` and do not need Unity.
- **Loading:** `ShaderFog` loads the bundle with `AssetBundle.LoadFromFile` (new reference: UnityEngine.AssetBundleModule)
  and logs `Shaders: bundle loaded (lit fog ok, unlit fog ok)`. Without the bundle, first person falls back to the
  fog sheets.

## Shaders

`CasualtyVR/Sprite-Lit-Fog` and `CasualtyVR/Sprite-Unlit-Fog` are URP 14.0.12's `Sprite-Lit-Default` and
`Sprite-Unlit-Default`, generated from the package source and extended for distance fog and desktop flashlight lighting.

- **The fog:** in the Universal2D pass, the world position goes from vertex to fragment, and the colour is blended
  toward `_CvrFogColor` by distance from the camera, using `_CvrFogParams` (start, end, maximum, on). The fog code is
  in `CasualtyVRFog.hlsl`.
- **Desktop flashlight:** while desktop first person is active, the held `Light2D` is suppressed during the camera
  render and its position, direction, range and cone are supplied as shader globals. Lit and unlit sprite passes
  evaluate a soft spotlight per fragment using `CasualtyVRFlashlight.hlsl`; the color fades with distance and outside
  the cone. This lights the visible scene surfaces rather than drawing the game's 2D light shape.
- **Shader swapping:** the same fog variants are also enabled for the duration of desktop first person so the
  flashlight can affect existing game materials. The globals are zeroed after rendering; the normal shaders are
  restored when desktop first person ends.
- **Compiler workaround:** the D3D11 compiler raised an internal error ("invalid access of unbound variable") on the
  no-keyword lit variant until the lit colour went into a local first and the fog maths used float precision.

## Use: smooth first-person fog (`ShaderFog.cs`)

- **When it applies:** on foggy layers (mist colour) and with Void sides (black), in first person.
  - Every material using URP's `Sprite-Lit-Default` or `Sprite-Unlit-Default` is switched to the fog variant: blocks,
    walls, sprites, creatures, items and our copies.
  - Materials created at runtime are picked up by a rescan every 2 s, and all are switched back when first person or
    the fog ends.
- **The monitor is unaffected:** the fog values are set only while a headset eye renders; otherwise the amount is 0,
  which gives URP's exact output.
- **Distances:**
  - Fog: from 2 blocks to min(30, view distance).
  - Void darkness: to the view distance, which also hides the far-plane cut.
- **What this replaces:** the stepped fog sheets, and the "characters and items are not fogged" limit.
- **Verified** (`sfog*`, `sfogpod*` captures): smooth fog in a cave and in the lifepod, no floor bands, your own arms
  unaffected, 90 fps.

## Next candidates for the bundle

- A depth-writing pass for the extruded body and arms, to fix wall ordering.
- Lights evaluated in world space instead of the 2D light texture, which would make first-person lighting exact.
