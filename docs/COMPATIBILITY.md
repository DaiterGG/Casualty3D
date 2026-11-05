# COMPATIBILITY — headsets, runtimes and Linux (2026-10-07, 1.2.0)

What the mod needs from an OpenXR runtime, what was changed for 1.2 to widen support, and what is still untested.
Tested hardware: Meta Quest 3 + Virtual Desktop (VDXR), and xr-sim (Quest 3 geometry) headless.

## Runtime requirements (native bridge, `src/CasualtyVR.XrBridge`)

| Need | Detail | Risk |
|---|---|---|
| `XR_KHR_D3D11_enable` | The only required extension. The game renders with D3D11 (Proton: DXVK) | VR refuses to start (with a message) if the game uses another API (`-force-vulkan`, `-force-d3d12`): the bridge would read the texture pointer as D3D11 |
| API 1.1 or 1.0 | Negotiated newest first (XR-008) | none |
| `PRIMARY_STEREO`, 2 views | Each eye camera uses its own pose and asymmetric FOV, so canted displays (Index ~5°, Pimax ~10°) render correctly | none |
| `LOCAL` reference space | Seated origin, recentered by F9 | none |
| `OPAQUE` blend mode | Passthrough is the app-side greenscreen | none |
| Swapchain format | `R8G8B8A8_UNORM_SRGB` > `B8G8R8A8_UNORM_SRGB` > the UNORM pair. The C# side makes the eye RTs in the same family (CopyResource) | **1.2:** BGRA added for runtimes without RGBA8 |
| Adapter (LUID) | Logged. **1.2:** a mismatch is a warning, and xrCreateSession decides. Translation layers (wineopenxr + DXVK) can report different LUIDs for the same GPU; a real mismatch (laptop iGPU) fails at xrCreateSession with a message naming the Windows graphics setting | — |
| C runtime | **1.2:** static CRT (`CMAKE_MSVC_RUNTIME_LIBRARY` MultiThreaded). The bridge imports only `openxr_loader.dll` and `KERNEL32.dll`; `UnityPlayer.dll` has no VC++ dependency either, so a PC without the VC++ redistributable now works | — |

## Controllers (interaction profiles)

All suggested in `CreateActions`. Touch is suggested last, because xr-sim resolves to the last suggestion.

| Profile | Spec | Layout (actions: aim, trigger, squeeze, stick, stick click, A, B, X, Y, menu, haptic) |
|---|---|---|
| `khr/simple_controller` | core | aim, select = trigger, menu |
| `htc/vive_controller` | core | trackpad = stick, squeeze/click; right pad click = A, right menu = B, left menu = menu; left pad click = left stick click |
| `microsoft/motion_controller` | core | thumbstick; right pad click = A, right menu = B, left pad click = X, left menu = menu |
| `valve/index_controller` | core | left a/b = X/Y; **menu = left trackpad/force** (no menu button; system is reserved) |
| `hp/mixed_reality_controller` | `XR_EXT_hp_mixed_reality_controller`, enabled when present | Touch layout |
| `oculus/touch_controller` | core | reference layout |

- **Validation:** xr-sim checks every suggested path against its own tables for these profiles (all `XR_SUCCESS`).
  It drives only Touch, so the other layouts are untested on hardware.
- **Not added:** `bytedance/pico4_controller` (Pico through Virtual Desktop uses Touch; SteamVR remaps) and
  `meta/touch_controller_plus` (the Touch profile still works on Quest 3).

## Linux (Proton)

The game is Windows-only, so Linux means Proton. Untested; the pieces:

- **BepInEx:** the doorstop proxy `winhttp.dll` must load native, via Steam launch options
  `WINEDLLOVERRIDES="winhttp=n,b" %command%` (README).
- **OpenXR:** our bundled Khronos loader reads the active runtime from the Wine registry. Proton's `wineopenxr`
  bridges D3D11 swapchains (DXVK) to a Linux OpenXR runtime (SteamVR for Linux). This is the same path the Unity
  OpenXR plugin's games use.
- **Native code:** `LoadLibrary` by full path (XrNative.Preload), plain Win32 and D3D11 COM; no registry, no
  Windows-only APIs beyond what Wine implements. The adapter check change above is the main Proton-specific fix.
- **Shaders:** the bundle is D3D11 (DXVK runs it). The fog falls back to sheets if the bundle cannot load.
- **Window placement:** Unity's `Screen.MoveMainWindowTo`; empty by default since 1.2, so it does nothing unless set.

## Failure messages (ScreenNotice)

VR start failures now show on the game window, not only in the log.

| Case | Message |
|---|---|
| `xrCreateInstance` fails | No OpenXR runtime: start Virtual Desktop / SteamVR / Quest Link, make it active, press F10 |
| No HMD | The runtime sees no headset |
| xrCreateSession fails after an adapter mismatch | The laptop GPU hint |
| Not D3D11 | Remove `-force-vulkan` / `-force-d3d12` |
| Native preload fails | Reinstall |

Verified headless by pointing `XR_RUNTIME_JSON` at a missing file for one process (`Notice: ... no OpenXR runtime is
available ...`).

## Multiplayer: Casualties Together (KrokMP) 5.0.2 (2026-10-17)

Read from the decompiled `BepInEx/plugins/Together/CasualtiesMP.dll` (grade STATIC).

The user's first co-op test with 1.2.1 (HEADSET) found three problems:

- the co-op menus and windows were missing in VR, including the right-click window on other players;
- name tags sat under the players in first person;
- the alt-tab icon showed sideways in first person.

1.2.2 addresses all three, generically, in [IMGUI_RECEIPT.md](IMGUI_RECEIPT.md). The user confirmed it in co-op in
the headset on 2026-10-18 (HEADSET).

| Finding | Effect in VR | Grade |
|---|---|---|
| The co-op main menu, player-list side menu, player-interaction window and chat are IMGUI (`OnGUI`: `UIBullshit`, `UIMainMenu`, `UIInGame`, `Chat`) | **1.2.2:** captured into the headset on every panel (`ImguiCapture`); the laser hovers and clicks through posted mouse messages. Typing in chat needs the keyboard | STATIC; capture LIVE (stand-in window) |
| `UIBullshit` checks the cursor against its IMGUI rectangles using `Input.mousePosition` (our laser's virtual mouse) and sets `IS_CURSOR_OVERLAPPING`, which its `UIUtil.IsPointerOverUIElement` postfix reports as "over UI" | With the windows now visible, this is the flat game's behaviour: no world attack while pointing at a co-op window. In first person the laser's mouse is on the panel while it points at a window (`VrInput.OnModWindow`) | STATIC + INFERENCE |
| Name tag: a TextMeshPro `CharacterNametag_<name>` from `special/ExpTalkText`, parented to the character root (the Body's parent), set every LateUpdate 3 units **below** the body centre | **1.2.2:** in first person it is drawn 1.6 blocks above the head, facing you, at 1.7–3° per line (`WorldText`) | STATIC; LIVE (cloned character) |
| Status icons (`KrokoshaMP_CharacterStatusIcon_*`, the alt-tab one included): SpriteRenderers on the character root, sorting order 6000, scale 9 | **1.2.2:** in first person they turn to face you, at most 4° tall | STATIC; LIVE (cloned character) |
| `PauseHandler.Update` postfix keeps `Time.timeScale` in multiplayer; `SetTimeScale` prefix blocks slow-motion while anyone moves | Pause, the VR settings menu and the dashboard do not stop the game | STATIC |
| `PlayerCamera.HandleInput` prefix skips the game's input while the chat box has focus | Controllers do nothing while typing in chat (by design) | STATIC |
| Spectator mode: `PlayerCamera.main.following` = another player; the end screen is hidden | The diorama follows the spectated player (it follows the game's camera). First person stays on your own body (`PlayerCamera.main.body`): switch to the diorama (F6) to spectate | STATIC + INFERENCE |
| Remote players are clones of the local character (`ServerMain.CHARACTER_PREFAB`) | Our test does the same (`--cvr-dbg-clone`) | STATIC |
| `FluidManager.RenderFluids`: a counting prefix (`KrokV5Optimization.EvidenceParticles`) | No conflict with our postfix | STATIC |
| `Body.PickUpItem` and the `PlayerCamera.Try*` actions are patched to sync | First-person grabs call `Body.PickUpItem`, so they go through its sync | STATIC |
| `Camera.main` and `Input.mousePosition` are used for world aim (`Util.cs`) | Unaffected: the eye cameras are not tagged MainCamera, and the virtual mouse is in the game camera's screen space | STATIC |
| Its patcher `KrokMP/autoupdater_patcher.dll` (6.5 KB) was not examined | — | — |
