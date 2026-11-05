# CasualtyVR

VR mod for *Casualties: Unknown* (Unity 2022.3.62f3, Mono, URP 2D Renderer, orthographic camera, D3D11).
BepInEx 5 plugin (C#, net472) plus a native OpenXR bridge (C++, D3D11).

Machine-specific notes, if any, are in `CLAUDE.local.md` (not in git).

**Playbook:** clone https://github.com/phunkaeg/vr-modding-playbook into `VR Modding Playbook/` (git-ignored). Read its
`AGENTS.md` first, never the chapters end to end. Route by symptom (`docs/failure-atlas.md`), by recipe
(`docs/pattern-catalog.md`), or by earliest blocker (`docs/bottleneck-map.md`). Cite pattern IDs (`CAM-001`) and failure IDs
(`FAIL-CAM-001`) in this project's own docs.

- Plan: `docs/VR_PLAN.md` · Target facts: `docs/TARGET_PROFILE.md` · Phase 0: `docs/BASELINE_RECEIPT.md`, `docs/RENDER_PASS_CENSUS.md`
- Decompiled game code: `Working/decompiled/Assembly-CSharp` (ILSpy 9.1, `ilspycmd`). Make it yourself; never commit it.
- Managed source: `src/CasualtyVR.sln` (net472). Build + deploy: `tools/deploy-dev.ps1`.
- Release: `tools/package-release.ps1` -> `dist/CasualtyVR-<ver>.zip` (player docs in `release/`, receipts `docs/RELEASE_*.md`).
  Players get only Core + native bridge; logs are quiet unless `Debug.VerboseLog` or a headless run (`--cvr-autostart`).
- First person (1.1): plan `docs/FIRSTPERSON_PLAN.md`, receipts `docs/FP*_RECEIPT.md`, release `docs/RELEASE_1.1.md`.
  Perf: `tools/fp-perf.ps1` (docs/PERF_FP.md); frame-time spread and eye path: docs/PHASE0_RECEIPT.md, `tools/fp-path.ps1`. Shaders: `tools/build-shaders.ps1 -Unity <Unity.exe>` builds `src/CasualtyVR.Shaders`
  (Unity 2022.3.62f3, URP 14.0.12) into a bundle that is committed and deployed (docs/SHADERS.md). Layer modifiers:
  `--cvr-dbg-modifier <i>` (docs/LAYER_MODIFIERS.md).
- Headset feedback rounds: docs/FP7_RECEIPT.md (`--cvr-dbg-talk` speech + damage number, `--cvr-dbg-settings <tab> --cvr-dbg-reset`).
- **Build order: docs/BUILD_PLAN.md** (phases 0–8; merges IMPROVEMENT_PLAN, FIRSTPERSON_REVIEW, VRMENU_REVIEW).
  - Phase 1, the 3D settings menu on its own panel: docs/PHASE1_RECEIPT.md, test `tools/vr-menu.ps1` (flags `--cvr-dbg-settings <tab|all>`,
    `--cvr-dbg-settings-more`; current tabs are First person, 3D world, and Desktop).
  - Phase 2, first-person comfort (walk/run, wall fade, steady height, presets, scan staggering in `Scans.cs`): docs/PHASE2_RECEIPT.md,
    tests `tools/fp-comfort.ps1` and `tools/fp-path.ps1 [-Preset Moderate] [-Extra --cvr-dbg-nofilter]`; flags `--cvr-dbg-preset <name>`,
    `--cvr-dbg-pathdump` (per-frame eye path). Verbose perf adds `perf: scans (slowest ms)`: match the main perf line with `perf: .* fwd`.
  - Phase 3, the menu in the game's look (`MenuArt.cs`: the game's uiBlock/uiBlockNano sprites, glow, tab icons),
    and the round pause-menu entry: docs/PHASE3_RECEIPT.md, `tools/vr-menu.ps1 -Only pause`.
    `--cvr-dbg-uidump` (probe) lists the game's UI images and saves their sprites to `BepInEx/CasualtyVR/uidump`.
  - Phase 4a, hands: grip pose in the bridge (`VrInput.GripPose`; rays stay on the aim pose), swing/throw recipes in `HandMotion.cs`,
    motion takes (`--cvr-dbg-replay <folder|synthetic>`, `tools/motion-takes.ps1 [-Takes <dir>]`): docs/PHASE4A_RECEIPT.md.
    fp-smoke `swing-gate` (`--cvr-dbg-spawn pistol|machete --cvr-dbg-hold`). fp-smoke reads the player log now.
  - Phase 4b, hand poses (`HandPoses.cs`, `VrArms.PlacePose`), grab affordance and glow, distance pull (`VrGrab`):
    docs/PHASE4B_RECEIPT.md. fp-smoke `affordance`, `pull`, `poses` (captures); `--cvr-dbg-pull-aim` logs the hand angles
    that point at an item; `--cvr-dbg-dumphands` also saves the character's arm sprites and logs their tint and size.
  - Phase 5, senses and movement: sound at the head (`Ears.cs`), vibration from game events (`GameHaptics.cs`), real
    crouch (`VrInput.RealCrouch`, `FirstPerson`), rope transitions: docs/PHASE5_RECEIPT.md. fp-comfort `crouch`,
    `sound`, `rope`, `haptics`; flags `--cvr-dbg-sound`, `--cvr-dbg-rope`, `--cvr-dbg-hurt <s>`, `--cvr-dbg-danger`.
- Review plan: docs/IMPROVEMENT_PLAN.md (groups A–E). Group A (input/session lifecycle, recenter heading, fault isolation,
  bridge log `BepInEx/CasualtyVR-bridge.log`): docs/LIFECYCLE_RECEIPT.md, test `tools/vr-lifecycle.ps1` (xr-sim session states,
  focus, validity; flags `--cvr-dbg-recenter <s>`, `--cvr-dbg-throw <s>`). Read the Unity player log in tests, not LogOutput.log (buffered).
- Mods' OnGUI windows in VR (1.2.2, `ImguiCapture`): docs/IMGUI_RECEIPT.md; `vr-lifecycle.ps1 -Only imgui`. Flags `--cvr-dbg-imgui`
  (probe test window), `--cvr-dbg-imgui-dump`, `--cvr-dbg-clone` (co-op-style remote player with name tag/icon), `--cvr-dbg-noimgui`.
  Co-op mod (Casualties Together) facts: docs/COMPATIBILITY.md.
- Release 1.2.0: docs/RELEASE_1.2.md; headsets/Proton: docs/COMPATIBILITY.md. Flags: `--cvr-dbg-lockpick` (with `--cvr-dbg-spawn dropcapsule`),
  `--cvr-dbg-fpoff <s>` (speed restore), `--cvr-dbg-settings all` (cycles tabs), `--cvr-dbg-dumphands` (logs full-rect tool tips: use those, not the trimmed PNGs).
  Smoke test: `tools/fp-smoke.ps1` (xr-sim head/hand scripts; flags `--cvr-dbg-fp`, `--cvr-dbg-cave`, `--cvr-dbg-spawn a,b`, `--cvr-dbg-hold`).
  Scenarios share game sessions (5 launches, ~5 min); `-Quick` runs the hands and swing sessions only (~2 min): use it while iterating.
- Never test VR start without `-XrSim` (it would start the developer's real OpenXR runtime); for no-runtime tests set
  `XR_RUNTIME_JSON` to a missing file for that process.

## Setting up a development copy

The repository holds no game files. Everything under `Working/` and `dist/` is local and git-ignored.

1. Copy the game (Demo 7.0.1) to `Working/game-dev` and install BepInEx 5.4.23.x (x64) into it. Run it once, then quit.
   Another location works with `-GameDir` on the tools scripts and `/p:GameDir=...` for MSBuild.
2. Isolate its saves from your real ones: patch `productName` in `CasualtiesUnknown_Data/globalgamemanagers` from
   `CasualtiesUnknown` to `CasualtyVRDevCopy` (same length; keep the original as `globalgamemanagers.orig`). See docs/BASELINE_RECEIPT.md.
3. `tools/deploy-dev.ps1` builds the managed projects, the native bridge (CMake, "Visual Studio 16 2019" generator, v142 toolset)
   and deploys them with the sandbox patcher. The OpenXR SDK files it needs are in `third_party/`.
4. Test tools: clone https://github.com/phunkaeg/xr-sim and https://github.com/phunkaeg/xr-tape into `tools/xr-sim` and
   `tools/xr-tape` (git-ignored) and build them as their READMEs say.

## Never touch the real game data

- The developer's own Steam install: never modify it, never launch it for testing.
- Real saves live in `LocalLow\Orsoniks\CasualtiesUnknown` and `HKCU\Software\Orsoniks\CasualtiesUnknown`.
- The dev copy `Working/game-dev` is isolated twice: productName patched to `CasualtyVRDevCopy` in `globalgamemanagers`
  (engine level) and the sandbox patcher (game level). **Always launch via `tools/launch-dev.ps1`**, which refuses to
  run if the identity patch is missing. Never launch through `renderdoccmd` (BepInEx then fails to load); use `-RenderDoc`.
- Tests that change the dev config (`Working/game-dev/BepInEx/config/casualtyvr.core.cfg`) must restore it afterwards.

## Running

- `tools/launch-dev.ps1 -Windowed --cvr-autostart --cvr-seed 1234 --cvr-quit`: unattended run, probe report + quit.
  Seed 1234 world hash `4947A0FE6D30C272`. Reports: `Working/game-dev/BepInEx/CasualtyVR/run-<stamp>/`.
- Plugins must run behaviour on their own `DontDestroyOnLoad` GameObject: the game destroys BepInEx's manager object.
- VR headless: `tools/launch-dev.ps1 -Windowed -XrSim -XrTape --cvr-vr --cvr-autostart --cvr-seed 1234 --cvr-quit`
  (the xr-tape check must pass 19/19). Compositor image: `tools/xr-sim/tools/xrsim-shot.ps1 -Dir $env:LOCALAPPDATA\xr-sim\casualtyvr`.
  Cost attribution: `tools/vr-perf-matrix.ps1`. Real headset: `tools/launch-dev.ps1 --cvr-vr` (F10 toggle, F9 recenter).
- UI in VR: docs/PHASE3_UI_RECEIPT.md (canvases moved to a pixel-equal UI camera; layers 29 desktop-only,
  30 UI, 31 VR-only). `WindowPlacement` can keep the game window on a chosen monitor (`Window.PreferredDisplay`, empty by default).
- Controllers: docs/PHASE3_INPUT_RECEIPT.md (Harmony on UnityEngine.Input; one OpenXR action per hand,
  because xr-sim ignores subaction paths). Drive them headless with `tools/xr-sim/tools/xrsim-cmd.ps1 -Dir
  $env:LOCALAPPDATA\xr-sim\casualtyvr "stick l 1 0"` etc. Probe test commands: `--cvr-command kill|!selfdestruct|!endsequence`.
- Never move a transform that has a Collider2D for rendering purposes (90 -> 10 fps, see docs/PHASE1_XR_RECEIPT.md).

## Tools

- **Regression: `tools/regress.ps1`** runs every suite (fp-smoke, vr-menu, vr-lifecycle, fp-comfort, motion-takes,
  xr-tape) with a timed summary, saves output to `Working/regress/` and restores the dev config. `-Quick` (5 launches)
  while iterating; the full run before a test build. Suites share game sessions where they can (starting the game is
  most of each run). Never run two suites at once: they share the game, xr-sim and the logs.
- `tools/xr-sim` (simulated OpenXR runtime, `tools/run-with-xrsim.ps1`), `tools/xr-tape` (`tools/Invoke-XrTape.ps1 -Check`).
- Builds: the native bridge (`src/CasualtyVR.XrBridge`, built by `tools/deploy-dev.ps1`) and xr-sim/xr-tape use CMake
  "Visual Studio 16 2019". The bridge links the CRT statically, so players need no Visual C++ runtime.
