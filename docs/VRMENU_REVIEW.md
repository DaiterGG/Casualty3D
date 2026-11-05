# VRMENU_REVIEW — the VR settings menu: usability and look (proposal for review, 2026-10-18)

This document reviews the in-game VR settings (`VrSettingsMenu.cs`, F12, or the pause menu's *VR* button) against
the game's own visual style and VR menu guidance, and proposes improvements. **Nothing is built yet.** The decisions
are in section 5.

Captures used (local, in `Working/menu-review/`; not committed):

- the headset view of each tab: `tab-*_left.png`, `pause_left.png`;
- the game window with its own pause, inventory and crafting screens: `flat-pause.png`, `flat-inventory.png`,
  `flat-crafting.png`.

## 1. The menu today

**Layout.** One canvas, 1240 × 950 reference pixels, drawn on the dashboard below the diorama with the rest of the
UI:

- a title, then six tabs: View, Blocks, 1st person, Hands, Controls, Passthrough;
- Recenter, Reset page and Done at the top right;
- a list of rows on each page, each `label  <  value  >`;
- the version at the bottom.

**Look.** An opaque near-black panel, flat grey buttons, a cyan selected tab and white text in the game's pixel
font. There are no frames, icons, glow or dividers.

**Size in the headset** (dashboard: 1.0 m wide for 1920 px, 0.93 m from your head):

| Element | Size on the panel | Angle at 0.93 m | Guidance |
|---|---|---|---|
| `<` / `>` buttons, most pages | 48 px = 25 mm | **1.5°** | Ray targets ≥ 2.5–3° ([Meta, hands UI][meta-hands]) |
| `<` / `>` buttons, Controls page (15 rows a column) | 39 px = 20 mm | **1.2°** | same |
| Row text (28 px, auto-shrinks to 14) | about 15 mm em | **0.9°** (down to 0.45°) | 14 dp minimum, 18 dp comfortable, at 500 dp/m ([Meta][meta-req], [Meta][meta-panel]): about 28–36 mm at 1 m, so roughly **twice** the current size |
| Whole menu | 0.65 m wide | — | — |

The captures confirm it: in the headset each row is about 8 px tall in a 1032-px eye image, and the hand cursor
covers two rows.

**Interaction:**

- Every setting is stepped with `<` and `>`, including continuous values (distances, sizes, strengths): many small
  clicks.
- Toggles and choice lists (On/Off, Mist/Haze/Off) also use the arrows; the value itself cannot be clicked.
- There is no explanation of a setting. The config file has a description for every one; the menu shows none.
- Hover feedback is only a slight tint; there is no vibration.
- In first person the menu is on the dashboard, used with the laser (touch works only for minigames).

**Organisation:**

- *View* mixes zoom, the window, the HUD, fog, the dashboard's position and the cursor (14 rows).
- *Hands* includes your body; *Controls* also holds the minigame hand offsets.
- There is no comfort page, although first person adds comfort settings (FP-8).
- Placement is typed numbers (dashboard distance, height, width) instead of moving the panel.

**Pause menu entry:** the game's pause menu has three round outlined icon buttons; ours is the word "VR" under the
gear (the cloned icon was cut off, so it was removed). It does not look like part of the game.

## 2. The game's own style (`flat-*.png`)

- **Frames:** glowing pale-blue / periwinkle outline frames (1–2 px bright line with a soft glow) around
  translucent dark-blue panels. The scene shows through.
- **Text:** the game's pixel font, large. Recipe rows in crafting are about 40 px tall at 1080p, with an icon and a
  green **+** accent.
- **Lists:** rows separated by thin outline dividers; a scroll bar on the right.
- **Buttons:** round outlined icon buttons (pause menu); icon tabs (crafting categories); a radial inventory.
- **Colour:** periwinkle lines, green for *add / good*, yellow for notices, red for a missing ingredient.

Our menu's opaque black box with grey buttons reads as a debug panel next to this.

## 3. VR menu guidance (distilled)

- **Target size:** ray targets at least 2.5–3°. Keep the visible button smaller inside a larger hit area
  ([Meta, hands UI][meta-hands]). Larger selection angles cut selection time ([Das & Borst][dasborst]).
- **Text:** 14 dp minimum, 18 dp comfortable, at 500 dp/m ([Meta][meta-req], [Meta][meta-panel]). Use high
  contrast; avoid light weights.
- **Distance and reach:** use the ray beyond arm's reach. Close panels used by touch should curve, so no control
  needs a full arm stretch ([Ultraleap][ultraleap]).
- **Touch (poke):** 22 × 22 mm targets with 12 mm between them ([Meta, Touch][meta-touch]).
- **Press feedback:** buttons raised off the panel that move in when pressed ([Ultraleap][ultraleap]). Haptics on
  hover and accept: the same "affordance channel" rule as for grabbing (playbook ch. 02).
- **Both inputs:** laser and touch, so either first instinct works (the Half-Life: Alyx design write-up, see
  FIRSTPERSON_REVIEW).
- **Placement:** calibrate anything spatial from inside the headset rather than with numbers (playbook ch. 02,
  "calibrate anything spatial from inside the headset").
- **Settings in the game's own UI style** (playbook ch. 03, "put your settings in the game's own options UI").

## 4. Proposals

### M1 Legible and easy to hit (first)

| Item | What | Size |
|---|---|---|
| M1a Menu panel of its own | While the VR settings are open, the dashboard shows only the menu's area of the UI texture, filling the panel. That alone makes everything about 1.5× bigger at the same panel width. The panel is 0.9 m wide at about 0.8 m, so targets are ≥ 2.5° and text ≥ 1.6° | S–M |
| M1b Fewer, bigger rows | At most 8–9 rows a page at about 80 px. Whole-row hit areas; the visible controls sit inside. A scroll list (as crafting) where a page needs more | M |
| M1c Description line | The hovered setting's own config description, shown in a footer strip (the text exists already) | S |

### M2 Controls that suit a pointer

| Item | What | Size |
|---|---|---|
| M2a Switches | On/Off as a switch you click anywhere on the row | S |
| M2b Choices | Short lists (Mist / Haze / Off, Snap / Smooth) as segmented buttons, all options visible | S |
| M2c Sliders | Continuous values as a slider: hold the trigger on it and drag, with fine `<` `>` at the ends and the value in real units (m, %, °/s) | M |
| M2d Feedback | Hover lifts the control and ticks the controller lightly; a click gives a stronger tick and the game's own UI click sound | S |
| M2e Row reset | A small "↺" on rows that differ from their default (alongside *Reset page*) | S |
| M2f Touch in first person | The menu becomes touchable within reach, as minigames already are, using the 22 mm / 12 mm spacing | S–M |

### M3 The game's look

| Item | What | Size |
|---|---|---|
| M3a Frames and panels | Translucent dark-blue panel with the game's glowing outline frame. Reuse the game's own UI sprites and materials from its crafting panel and pause buttons, found at runtime, so it matches exactly and needs no new art | M |
| M3b Rows and accents | Thin outline dividers between rows; periwinkle text for labels, white for values, green for *on*, yellow for warnings (for example "takes effect after restart") | S |
| M3c Tabs with icons | Small pixel icons on the tabs, as the crafting categories have (game sprites where they fit: hand, eye, cube…; else simple pixel icons in the same outline style) | S–M |
| M3d Pause-menu button | A round outlined *VR* button that matches the gear and skull, sized to fit inside the pause menu | S |

### M4 Organisation

| Item | What | Size |
|---|---|---|
| M4a Tabs by task | **Comfort** (new: FP-8 presets, turning, vignette, knocked-down view, speed) · **View** (diorama zoom, window, fog, blocks and depth) · **First person** (sides, faces, view distance, scale) · **Hands & body** · **Controls** · **Menus & HUD** (panel placement, HUD size, wrist HUD, cursor) · **Passthrough** · **About** (version, keys F6–F12, where the logs are) | M |
| M4b Basic / Advanced | Each page shows the common settings; an *Advanced* switch at the bottom shows the tuning rows (thicknesses, smoothing, minigame offsets, colour channels) | S |
| M4c Controls as a picture | A drawing of the controller (Touch outline in the game's line style) with each button's action next to it; click a label to change it. Instead of 21 identical rows | M–L |

### M5 Placement in VR

| Item | What | Size |
|---|---|---|
| M5a Grab to move | Grip the panel's frame and move or turn it where you want; it remembers that. The distance / height / width rows go to Advanced | M |
| M5b Opens in front | The menu opens facing you at a comfortable height, also when you are turned (uses the recenter heading) | S |

### Also in scope: the game's own menus in VR

The crafting, inventory and wound screens share the small-text problem on the dashboard. M1a's "fill the panel
with the active menu" can be applied to them too: zoom the dashboard to the open game menu's area.

Suggested order: M1, then M2a–d, then M3, then M4, then M5 and M2f.

## 5. Decisions (user, 2026-10-18)

| # | Question | Decision | Effect |
|---|---|---|---|
| V1 | How far to match the game's look | **Full, if practical** | M3 uses the game's own frames, glow and translucent panels; if reusing its sprites is impractical, say so and fall back towards "clean" |
| V2 | Where the menu shows | **Its own enlarged panel** | M1a |
| V3 | Continuous settings | **Sliders** | M2c |
| V4 | Advanced settings | **Hidden**, behind a button named *More settings* (or *More…*) | M4b, with that label |
| V5 | Controls page | **Controller picture** | M4c |
| V6 | Panel placement | **Keep the number rows** | M5a dropped; the rows stay (under *More settings*); M5b (opens in front) stays |
| V7 | Also zoom the game's own menus | **Yes** (2026-10-18, after seeing the zoomed VR menu): crafting, inventory, wound view | Built in Phase 3, see PHASE3_RECEIPT |

## 6. Testing

- **Headless:** capture every page with `--cvr-dbg-settings all` and xr-sim shots (as for this review). A check
  computes each interactive element's angular size from its rect and the panel pose, and fails anything under
  2.5°. Laser clicks are scripted on a switch, a segment, a slider and a tab.
- **Headset:** readability at the default distance; hitting rows with the laser and by touch in first person;
  dragging a slider; moving the panel.

## Sources

[meta-hands]: https://developers.meta.com/vr/design/hands-ui-best-practices/
[meta-touch]: https://developers.meta.com/vr/design/touch_bp/
[meta-req]: https://developers.meta.com/horizon/documentation/android-apps/design-requirements
[meta-panel]: https://developers.meta.com/horizon/essentials/horizon-os-panel-sizing/
[ultraleap]: https://docs.ultraleap.com/xr-guidelines/Components/ui-panels.html
[dasborst]: https://vrlab.cmix.louisiana.edu/wp-content/uploads/2025/04/Das_and_Borst_ISVC_VRMenus.pdf

- **Meta:** hands UI best practices · Touch best practices · Android-app design requirements · Horizon OS panel
  sizing
- **Ultraleap:** XR guidelines, UI panels
- **Research:** Das & Borst, *VR menus* (ISVC)
- **VR Modding Playbook:** ch. 02 (discoverability, calibrate spatial things in the headset), ch. 03 (settings in
  the game's own options UI)
- **This project:** docs/FIRSTPERSON_REVIEW.md (laser plus touch; FP-8 comfort settings)
