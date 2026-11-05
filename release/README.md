# CasualtyVR

A 3D first-person mod for **Casualties: Unknown** (Demo, game version 7.0.1). It puts the camera inside the
character's world, turning the terrain into solid blocks with depth. The desktop mode uses mouse-look and the
keyboard; the optional VR mode retains the 3D diorama and controller support.

## Requirements

- Casualties: Unknown Demo 7.0.1 (Steam). Windows, or Linux through Proton (see *Linux* below).
- [BepInEx 5.4.23.x](https://github.com/BepInEx/BepInEx/releases), **x64**, **Unity Mono** build (`BepInEx_win_x64_5.4.23.x.zip`)
- A Direct3D 11 GPU.
- Optional VR: an OpenXR runtime with a headset connected. It was developed and tested on a Meta Quest 3 with
  Virtual Desktop; other headsets and runtimes should work but are untested.

## Install

1. Install BepInEx 5 (x64) into the game folder, which contains `CasualtiesUnknown.exe`. Run the game once so
   that BepInEx creates its folders, then quit.
2. Extract this archive into the game folder, so you get
   `BepInEx\plugins\CasualtyVR\CasualtyVR.Core.dll` and `BepInEx\plugins\CasualtyVR\native\`.
3. Start the game. Desktop first-person mode starts automatically; press **F6** to switch back to the normal view.
   To use optional VR, start your OpenXR runtime and press **F10**. To start in VR every time, set
   `StartVrOnLaunch = true` under `[VR]` in
   `BepInEx\config\casualtyvr.core.cfg`.

To uninstall, delete `BepInEx\plugins\CasualtyVR` (and `BepInEx\config\casualtyvr.core.cfg` if you want to
reset the settings).

## Controls

Meta Quest Touch (and Pico, through SteamVR or Virtual Desktop):

| Control | Action |
|---|---|
| Right controller pointing | Mouse cursor (aim, click, menus) |
| Right trigger | Attack / click |
| Right grip | Interact / right-click |
| Left stick | Walk, climb, crouch |
| A | Jump |
| B | Craft |
| X | Inventory |
| Y | Wound view |
| Left trigger, right stick down | Switch hands |
| Left grip | Alt view |
| Left stick click, right stick up | Throw (hold to charge, release to throw, tap to drop) |
| Both grips held | Ragdoll |
| Left menu button | Pause |

Other controllers use the same layout, with these differences:

| Controllers | Differences |
|---|---|
| Valve Index | Left A / B are X / Y. There is no menu button: **press the left trackpad firmly** to pause. |
| HP Reverb G2 | Same as Touch. |
| Windows Mixed Reality | Right trackpad click = A, right menu = B, left trackpad click = X, left menu = pause. No Y. |
| HTC Vive wands | Trackpads act as sticks. Right trackpad click = A, right menu = B, left menu = pause. No X / Y. |

The controller mappings are also available in the `[Controls]` section of the config file. The mapping goes
through the game's own key binds, so rebinding the game's keys also carries over. The Index, Vive and Windows
Mixed Reality layouts are new in 1.2 and have not been tried on real hardware yet: reports are welcome.

Keyboard: **W / S** move forward/back in desktop first person, **Shift** crouch, **Space** jump, **Tab** inventory,
**F6** toggle first-person view,
**F10** start/stop VR, **F9** recenter, **Page Up / Page Down** zoom, **F11** show the menu panel,
**F12** 3D settings, **F8** 3D blocks on/off.

## 3D settings

Open the pause menu and press the round **3D** button (beside the gear), or press **F12**. Use the mouse to choose
a tab or setting; changes are saved when you close the menu with **Close** or **Esc**. The menu contains:

- **First person:** tunnel width and sides, view distance, object facing, target highlight, sound positioning and
  world scale. Camera smoothing is under *More settings*.
- **3D world:** 3D blocks, fog, object extrusion, character thickness, and (under *More settings*) liquid depth and
  the overall 3D depth scale.
- **Desktop:** mouse sensitivity and keyboard controls.

All settings are also in `BepInEx\config\casualtyvr.core.cfg`, with a few extra ones (for example
`ThicknessLayers` under `[Voxels]`).

## First-person mode (experimental)

Press **F6** to switch first-person view on or off. In desktop mode the mouse turns the view; **W / S** move forward
and backward, **Shift** crouches, **Space** jumps, and mouse buttons retain the game's actions. The cursor is released
for the game's Tab inventory and pause menus, and captured again when gameplay resumes; mouse sensitivity can be adjusted
under `[Desktop]` in the config. In VR, open 3D settings > *First person*
to play from the character's eyes. You stand inside the 3D blocks, looking along the tunnels; loading screens and
full-screen menus use the normal view. In desktop first person, the held flashlight's 2D light is suppressed and a
soft, camera-directed spotlight is applied to the 3D scene instead of drawing a triangle pointing up in the play plane.
Press **F9** (or *Recenter*) to put your head at the character's head.

**Moving**

| Control | Action |
|---|---|
| Left stick forward/back, part way | Walk the way you face (1.4 m/s) |
| Left stick forward/back, all the way | Run (6.5 m/s by default) |
| Right stick up/down | Climb / crouch |
| Lower your head (about 30 cm) | Crouch (stand up to stop) |
| Right stick flick sideways | Turn around (or smooth turning, in the settings) |
| A | Jump |

**Your hands**

The right controller is the character's active hand, and the left is the other hand.

| Gesture | Action |
|---|---|
| Grip near an item | Pick it up into that hand (a glowing hand shows on it and the controller ticks when it is within reach) |
| Point at an item further off, grip, then flick your hand towards you | Pull it into your hand (in sight, within 10 blocks) |
| Grip near a crate, door or machine | Use it |
| Grip near a rope or ladder, then pull down | Climb, hand over hand |
| Let go (slowly / while moving) | Drop / throw, harder the faster your hand moves, from where your hand is |
| Held item to your chest, let go | Store it in the inventory |
| Empty hand to your chest, grip | Open the inventory (use it with the pointer) |
| Food, drink or pills held to your mouth | Eat, drink or take them |
| Fast swing of the right hand | Attack along the swing, once per swing (bare hands and melee tools; not with a gun) |
| Trigger | Attack or use, aimed where your hand points |
| Right grip with nothing in reach | The usual interact at the pointer |
| Minigames | Shown within reach: touch them with your hand, click with the trigger |

You see your own arms, with their wounds, clothing and what they hold, and your body and both legs when you look
down. Items, creatures and objects turn to face you. What your character says shows as a caption in front of you;
other characters' speech and damage numbers turn to face you. When lockpicking, the tip of the pick (or your
finger) is exactly where it acts.

## Linux (Proton)

The game is a Windows game, so on Linux it runs through Proton. This has not been tested yet; reports are
welcome. What it needs:

1. BepInEx needs a DLL override to load under Proton. In Steam, open the game's *Properties* > *Launch Options*
   and enter: `WINEDLLOVERRIDES="winhttp=n,b" %command%`
2. A Linux OpenXR runtime that Proton can reach. Proton passes OpenXR to SteamVR for Linux; other runtimes
   (Monado, WiVRn) need their own Proton setup.
3. The game must run with Direct3D 11 (Proton's default, through DXVK). Do not add `-force-vulkan`.

## Known limits

- The game's fog shader cannot render in VR. On foggy layers it is replaced by a VR mist (or a haze, or nothing).
- While the passthrough greenscreen is on, the game's colour grading is turned off in the headset so that the key
  colour stays exact.
- Very busy scenes (heavy rain, many creatures) can drop below 90 fps.
- Only the Demo 7.0.1 has been tested. Other game versions and other mods are untested.
- Other mods' windows (drawn with Unity's OnGUI, like the Casualties Together co-op mod's menus and chat) are shown
  in the headset and work with the laser; typing (chat) still needs the keyboard.
- The controller hands and the hand cursor use the game's own hand art.
- First person is experimental. The game is drawn in 2D, so its lights, flat images and 2D shadows are adapted to
  work from inside the world: lighting is approximate, and creatures are seen side-on.

## Troubleshooting

- **No VR:** if VR cannot start, a message at the top of the game window says why. Check that your OpenXR runtime
  is running and set as the active runtime, then press F10. Details are in `BepInEx\LogOutput.log`; lines start
  with `[CasualtyVR]`.
- **"Different graphics card than the headset" (laptops):** Windows runs the game on the integrated GPU. In
  Windows Settings > Display > Graphics, set `CasualtiesUnknown.exe` to *High performance*.
- **"VR needs Direct3D 11":** remove `-force-vulkan` or `-force-d3d12` from the game's launch options.
- **The view is too low or high, off to one side, or the game is not in front of you:** face the way you want
  the game to be and press F9 (or *Recenter* in the settings). It sets both the position and the direction.
- **"The VR session ended":** the headset disconnected or its PC app (Virtual Desktop, SteamVR...) closed. Start
  it again, then press F10.
- **Diagnostics for a bug report:** set `VerboseLog = true` under `[Debug]` in the config, reproduce the problem,
  and send `BepInEx\LogOutput.log` and `BepInEx\CasualtyVR-bridge.log` (`CasualtyVR-bridge.prev.log` is the
  previous game session's).
