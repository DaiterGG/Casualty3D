using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace CasualtyVR
{
    /// <summary>
    /// CasualtyVR entry point. F10 starts/stops VR (VR.StartVrOnLaunch starts it with the game; --cvr-vr forces it). F9 recenters.
    /// </summary>
    [BepInPlugin(Guid, "CasualtyVR", NumericVersion)]
    [BepInProcess("CasualtiesUnknown.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "casualtyvr.core";
        public const string Version = "1.3.0-test14";
        /// <summary>BepInEx needs a plain version number (test builds carry a suffix in Version).</summary>
        public const string NumericVersion = "1.3.0";
        /// <summary>The game version this release was tested with (Application.version).</summary>
        public const string TestedGameVersion = "7.0.1";
        /// <summary>Another game version: logged at start, and noticed on the monitor when VR starts (FAIL-PACK-015).</summary>
        internal static bool UntestedGameVersion;
        /// <summary>The native bridge's own log, flushed per line (VrRig.StartVr); the previous run's is kept as .prev.</summary>
        internal static string BridgeLogPath;

        internal static ManualLogSource Log;
        internal static string NativeDir;
        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<KeyCode> RecenterKey;
        internal static ConfigEntry<KeyCode> ZoomInKey;
        internal static ConfigEntry<KeyCode> ZoomOutKey;
        internal static ConfigEntry<KeyCode> DashboardKey;
        internal static ConfigEntry<string> PreferredDisplay;
        internal static ConfigEntry<float> FrameMargin;
        internal static ConfigEntry<float> FrameFade;
        internal static ConfigEntry<float> DashboardDistance;
        internal static ConfigEntry<float> DashboardHeight;
        internal static ConfigEntry<float> DashboardWidth;
        internal static ConfigEntry<float> MenuWidth;
        internal static ConfigEntry<float> SettingsDistance;
        internal static ConfigEntry<float> SettingsHeight;
        internal static ConfigEntry<float> SettingsWidth;
        internal static ConfigEntry<bool> ZoomGameMenus;
        internal static ConfigEntry<float> CursorSize;
        internal static ConfigEntry<string> LaserHand;
        internal static ConfigEntry<bool> ShowLaser;
        internal static ConfigEntry<float> UnitsPerMetre;
        internal static ConfigEntry<float> DioramaDistance;
        internal static ConfigEntry<float> PopupScale;
        internal static ConfigEntry<bool> FlipY;
        internal static ConfigEntry<float> MinigameHandX;
        internal static ConfigEntry<float> MinigameHandY;
        internal static ConfigEntry<bool> StartInVr;
        internal static ConfigEntry<bool> VerboseLog;
        /// <summary>Diagnostics in the log (perf, input, self-tests): config, --cvr-verbose, or any headless test run.</summary>
        internal static bool Verbose;
        internal static ConfigEntry<string> FogMode;
        internal static ConfigEntry<float> FogHaze;
        internal static ConfigEntry<float> FogMist;
        internal static readonly Color FogMistColour = new Color(0.16f, 0.18f, 0.21f);
        internal static ConfigEntry<string> CursorStyle;
        internal static ConfigEntry<float> HandCursorSize;
        internal static ConfigEntry<bool> ShowHands;
        internal static ConfigEntry<float> HandSize;
        internal static ConfigEntry<KeyCode> SettingsKey;
        internal static ConfigEntry<float> HudScale;
        internal static ConfigEntry<bool> Greenscreen;
        internal static bool GreenscreenOn => Greenscreen.Value || Array.IndexOf(Environment.GetCommandLineArgs(), "--cvr-dbg-greenscreen") >= 0;
        internal static ConfigEntry<Color> GreenscreenColor;
        internal static ConfigEntry<bool> HoverHighlight;
        internal static ConfigEntry<string> FpHighlight;
        internal static ConfigEntry<bool> VrRain;
        internal static readonly System.Collections.Generic.Dictionary<string, ConfigEntry<string>> ControlBinds = new System.Collections.Generic.Dictionary<string, ConfigEntry<string>>();
        internal static ConfigEntry<bool> VoxelsOn;
        internal static ConfigEntry<KeyCode> VoxelKey;
        internal static ConfigEntry<float> VoxelFront;
        internal static ConfigEntry<float> VoxelBack;
        internal static ConfigEntry<float> VoxelBudgetMs;
        internal static ConfigEntry<bool> SlabsOn;
        internal static ConfigEntry<bool> ObjectDepthOn;
        internal static ConfigEntry<float> ObjectDepth;
        internal static ConfigEntry<bool> LiquidDepthOn;
        internal static ConfigEntry<float> LiquidSurfaceAlpha;
        internal static ConfigEntry<string> SeeThroughBlocks;
        internal static ConfigEntry<int> SlabLayers;
        internal static ConfigEntry<float> SlabThickness;
        internal static ConfigEntry<bool> FpOn;
        /// <summary>--cvr-dbg-fp: start in first person for this session only (not saved).</summary>
        internal static bool FpForced = Environment.GetCommandLineArgs().Contains("--cvr-dbg-fp");
        internal static bool FpWanted => FpOn.Value || FpForced;
        internal static ConfigEntry<KeyCode> FpKey;
        internal static ConfigEntry<string> FpSides;
        internal static ConfigEntry<float> FpUnitsPerMetre;
        internal static ConfigEntry<float> FpSmoothing;
        internal static ConfigEntry<float> FpCorridor;
        internal static ConfigEntry<float> FpViewDistance;
        internal static ConfigEntry<bool> FpFaceLights;
        internal static ConfigEntry<bool> FpLookWithHead;
        internal static ConfigEntry<bool> FpNoLightShadows;
        internal static ConfigEntry<string> FpAimWith;
        internal static ConfigEntry<bool> FpBillboards;
        internal static ConfigEntry<bool> FpFaceObjects;
        internal static ConfigEntry<bool> FpArms;
        internal static ConfigEntry<bool> FpBody;
        internal static ConfigEntry<float> FpBodyDepth;
        internal static ConfigEntry<float> FpBodyBack;
        internal static ConfigEntry<float> FpMoveSpeed;
        internal static ConfigEntry<float> FpArmThickness;
        internal static ConfigEntry<bool> FpMinigameTouch;
        internal static ConfigEntry<bool> Haptics;
        internal static ConfigEntry<float> HapticStrength;
        internal static ConfigEntry<string> FpGrabMode;
        internal static ConfigEntry<bool> FpSwingAttack;
        internal static ConfigEntry<float> FpVoidDarkness;
        internal static ConfigEntry<bool> FpBlackout;
        internal static ConfigEntry<float> FpVignette;
        internal static ConfigEntry<string> FpRagdollView;
        internal static ConfigEntry<string> FpTurning;
        internal static ConfigEntry<float> FpTurnSpeed;
        internal static ConfigEntry<bool> FpAnalogWalk;
        internal static ConfigEntry<float> FpWalkSpeed;
        internal static ConfigEntry<bool> FpSteadyHeight;
        internal static ConfigEntry<bool> FpSnapBlink;
        internal static ConfigEntry<bool> FpWallFade;
        internal static ConfigEntry<float> FpEyeUnits;
        internal static ConfigEntry<bool> FpHandPoses;
        internal static ConfigEntry<bool> FpGrabGlow;
        internal static ConfigEntry<bool> FpDistancePull;
        internal static ConfigEntry<bool> FpSoundAtHead;
        internal static ConfigEntry<bool> FpRealCrouch;
        internal static ConfigEntry<float> FpCrouchDepth;
        internal static ConfigEntry<float> DesktopMouseSensitivity;
        internal static ConfigEntry<float> DesktopFieldOfView;
        internal static ConfigEntry<bool> DesktopMinimap;
        internal static ConfigEntry<KeyCode> DesktopDioramaKey;
        internal static ConfigEntry<bool> HapticDamage;
        internal static ConfigEntry<bool> HapticHits;
        internal static ConfigEntry<bool> HapticHeartbeat;

        private void Awake()
        {
            Log = Logger;
            NativeDir = Path.Combine(Path.GetDirectoryName(Info.Location), "native");
            ToggleKey = Config.Bind("VR", "ToggleKey", KeyCode.F10, "Start/stop VR");
            RecenterKey = Config.Bind("VR", "RecenterKey", KeyCode.F9, "Re-anchor the diorama in front of your current head position");
            ZoomInKey = Config.Bind("VR", "ZoomInKey", KeyCode.PageUp, "Zoom the diorama in (saved to UnitsPerMetre)");
            ZoomOutKey = Config.Bind("VR", "ZoomOutKey", KeyCode.PageDown, "Zoom the diorama out (saved to UnitsPerMetre)");
            PreferredDisplay = Config.Bind("Window", "PreferredDisplay", "", "Keep the game window on the display with this resolution (e.g. 1920x1080) or this display name. Empty = leave the window alone.");
            DashboardKey = Config.Bind("VR", "DashboardKey", KeyCode.F11, "Show/hide the UI dashboard panel (it also opens automatically for menus and minigames)");
            UnitsPerMetre = Config.Bind("Diorama", "UnitsPerMetre", 25f, "Game world units (blocks) per metre; lower = zoomed in. 25 makes the 45-block-high game view 1.8 m tall.");
            FrameFade = Config.Bind("Diorama", "FrameFade", 0.5f, "Width of the soft fade at the window edge, as a fraction of the view's half-height (0 = hard edge).");
            FrameMargin = Config.Bind("Diorama", "FrameMargin", 1f, "Size of the VR window opening relative to the flat game's view (1 = see exactly what the flat game shows, 0 = no frame).");
            DashboardDistance = Config.Bind("Dashboard", "DistanceMetres", 0.75f, "Forward distance of the menu/minigame dashboard from the seated head origin.");
            DashboardHeight = Config.Bind("Dashboard", "HeightMetres", -0.55f, "Height of the dashboard relative to the head (negative = below).");
            LaserHand = Config.Bind("Controllers", "LaserHand", "Right", new ConfigDescription("Which controller's laser acts as the mouse.", new AcceptableValueList<string>("Right", "Left")));
            ShowLaser = Config.Bind("Controllers", "ShowLaserBeam", false, "Draw the pointer beam from the controller (the cursor always marks where it points).");
            CursorSize = Config.Bind("Dashboard", "CursorSizeMetres", 0.025f, "Size of the mouse cursor drawn on the VR panels.");
            MenuWidth = Config.Bind("Dashboard", "MainMenuWidthMetres", 1.3f, "Width of the main-menu panel, shown at the diorama's position and distance.");
            DashboardWidth = Config.Bind("Dashboard", "WidthMetres", 1.0f, "Dashboard width; height follows the game window's aspect.");
            // The VR settings menu on its own panel (docs/VRMENU_REVIEW.md M1a, M5b): it opens in front of you, sized so
            // every control is at least 2.5 degrees across and the text about 16 dp.
            SettingsDistance = Config.Bind("Dashboard", "SettingsDistanceMetres", 0.8f, "Distance of the VR settings panel from your head when it opens.");
            SettingsHeight = Config.Bind("Dashboard", "SettingsHeightMetres", -0.12f, "Height of the VR settings panel's centre relative to your eyes when it opens (negative = below).");
            SettingsWidth = Config.Bind("Dashboard", "SettingsWidthMetres", 0.95f, "Width of the VR settings panel.");
            ZoomGameMenus = Config.Bind("Dashboard", "ZoomGameMenus", true, "Show the game's crafting, health (wound view) and trade screens, and the inventory in first person, on their own panel in front of you at the VR settings' size, instead of the whole screen on the dashboard.");
            DioramaDistance = Config.Bind("Diorama", "DistanceMetres", 1.2f, "Distance from the seated head origin to the game plane.");
            PopupScale = Config.Bind("Diorama", "PopupScale", 1f, "Multiplier on the pop-up-book layer depths (0 = flat card).");
            VoxelsOn = Config.Bind("Voxels", "Enabled", true, "Show the terrain as 3D blocks in first-person mode.");
            VoxelKey = Config.Bind("Voxels", "ToggleKey", KeyCode.F8, "Switch between 3D blocks and the flat terrain while in VR.");
            VoxelFront = Config.Bind("Voxels", "FrontDepth", 1.0f, "How far the block faces stand out in front of the play plane, in blocks.");
            VoxelBack = Config.Bind("Voxels", "BackDepth", 2.0f, "How far the blocks reach behind the play plane, in blocks (tunnels are this deep).");
            VoxelBudgetMs = Config.Bind("Voxels", "BuildBudgetMs", 2f, "Time per frame spent building block meshes (at least one chunk per frame while any are pending).");
            SeeThroughBlocks = Config.Bind("Voxels", "SeeThroughBlocks", "7,21,25", "Block ids drawn as see-through panes instead of solid blocks (7 glass, 21 scaffolding, 25 leaves).");
            LiquidDepthOn = Config.Bind("Voxels", "LiquidDepth", true, "Liquids fill tunnels front to back, with a surface across the depth.");
            LiquidSurfaceAlpha = Config.Bind("Voxels", "LiquidSurfaceOpacity", 0.35f, "Opacity of the liquid surface that spans the tunnel depth (0-1).");
            ObjectDepthOn = Config.Bind("Voxels", "ObjectExtrusion", true, "Give world objects (drop capsules, crates, doors, machines) real 3D depth, extruded from their sprites.");
            ObjectDepth = Config.Bind("Voxels", "ObjectDepth", 1.0f, "How deep extruded world objects are, in blocks.");
            SlabsOn = Config.Bind("Voxels", "CharacterThickness", true, "Give characters and items some thickness in the headset (stacked copies behind each sprite).");
            SlabLayers = Config.Bind("Voxels", "ThicknessLayers", 3, "Copies stacked behind each character/item sprite (1-8).");
            SlabThickness = Config.Bind("Voxels", "ThicknessDepth", 0.3f, "How thick characters and items are, in blocks.");
            VrRain = Config.Bind("Diorama", "Rain", true, "Show the game's rain in the headset as falling streaks (the game's own rain overlay cannot render in VR).");
            HoverHighlight = Config.Bind("Diorama", "HoverHighlight", true, "Show the game's glow on the object under the cursor in the headset.");
            FpHighlight = Config.Bind("FirstPerson", "Highlight", "Off", new ConfigDescription("First person: what the game highlights under your hand. Off = nothing (the building preview still shows); Blocks = the striped marker on the block you aim at (mining, building); All = also the outline of items and objects.", new AcceptableValueList<string>("Off", "Blocks", "All")));
            Greenscreen = Config.Bind("Passthrough", "Greenscreen", false, "Paint everything outside the game window, and the game's backmost layer, in a key colour (for passthrough chroma keying, e.g. Virtual Desktop).");
            GreenscreenColor = Config.Bind("Passthrough", "GreenscreenColour", new Color(0f, 1f, 0f, 1f), "Key colour for the passthrough greenscreen.");
            SettingsKey = Config.Bind("VR", "SettingsKey", KeyCode.F12, "Open/close the 3D settings menu (also on the pause menu's 3D button)");
            HudScale = Config.Bind("Dashboard", "HudScale", 1f, "Size of the game's HUD and menus while in VR (scales the game's main canvas).");
            // New keys (2026-10-04): hands now default off and the hand cursor bigger; old keys are ignored.
            ShowHands = Config.Bind("Controllers", "ShowControllerHands", false, "Draw the game's hand art at the controllers.");
            HandSize = Config.Bind("Controllers", "HandSizeMetres", 0.09f, "Height of the controller hands.");
            CursorStyle = Config.Bind("Dashboard", "CursorStyle", "Hand", new ConfigDescription("Mouse cursor in the headset: the game's hand (cut at the wrist; follows trigger/grip and the character's active hand), an arrow, or none.", new AcceptableValueList<string>("Hand", "Cursor", "Off")));
            HandCursorSize = Config.Bind("Dashboard", "HandCursorHeightMetres", 0.07f, "Height of the hand cursor (CursorStyle = Hand).");
            // FogMode key renamed (2026-10-04) so the new default (Mist) applies over the old saved "Haze".
            FogMode = Config.Bind("Diorama", "FogStyle", "Mist", new ConfigDescription("Foggy layers in the headset (the game's fog shader renders opaque in VR): Mist = steady unlit veil, clear around you and thicker with distance; Haze = lit haze (can shimmer with head movement); Off.", new AcceptableValueList<string>("Mist", "Haze", "Off")));
            FogMist = Config.Bind("Diorama", "FogMistDensity", 0.6f, "Opacity of the VR mist at its thickest (0-1), on Foggy layers.");
            FogHaze = Config.Bind("Diorama", "FogHazeOpacity", 0.3f, "Opacity of the VR fog haze on Foggy layers (0-1).");
            // Off by default (user, 2026-10-04); new key so configs saved with the old default (on) do not keep it.
            StartInVr = Config.Bind("VR", "StartVrOnLaunch", false, "Start VR as soon as the game starts (needs an OpenXR runtime: Virtual Desktop, SteamVR, Quest Link...). Off: press the ToggleKey (F10) to start VR.");
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Write diagnostics to the BepInEx log (frame rate, controller state, UI self-tests).");
            {
                var args = Environment.GetCommandLineArgs();
                Verbose = VerboseLog.Value || args.Contains("--cvr-verbose") || args.Contains("--cvr-autostart");
            }
            MinigameHandX = Config.Bind("Controllers", "MinigameHandOffsetX", 0f, "In VR, shift the minigame hand image right (+) or left (-) of where clicks land, in hand-image pixels (mirrored for the left hand).");
            MinigameHandY = Config.Bind("Controllers", "MinigameHandOffsetY", 0f, "In VR, shift the minigame hand image up (+) or down (-) relative to where clicks land, in hand-image pixels.");
            FpOn = Config.Bind("FirstPerson", "Enabled", false, "First-person view: your head at the character's head, inside the 3D blocks (experimental). Menus and loading screens still use the normal view.");
            FpKey = Config.Bind("FirstPerson", "ToggleKey", KeyCode.F6, "Switch between the diorama and first person in VR, or toggle desktop first-person mode.");
            DesktopMouseSensitivity = Config.Bind("Desktop", "MouseSensitivity", 2f, "Mouse-look sensitivity in desktop first-person mode.");
            DesktopFieldOfView = Config.Bind("Desktop", "FieldOfView", 90f, new ConfigDescription("Camera field of view in desktop first-person mode.", new AcceptableValueRange<float>(40f, 120f)));
            DesktopMinimap = Config.Bind("Desktop", "Minimap", true, "Show a lightweight terrain sideview minimap in desktop first-person mode.");
            DesktopDioramaKey = Config.Bind("Desktop", "DioramaViewKey", KeyCode.F7, "Return to the game's original side-on diorama camera from desktop first-person mode.");
            FpSides = Config.Bind("FirstPerson", "Sides", "CaveWalls", new ConfigDescription("What closes the tunnels' long sides in first person: the game's cave walls, or a black void.", new AcceptableValueList<string>("CaveWalls", "Void")));
            FpUnitsPerMetre = Config.Bind("FirstPerson", "UnitsPerMetre", 0f, "World scale in first person, blocks per metre. 0 = automatic: the character's eye height matches a 1.6 m standing eye height.");
            FpSmoothing = Config.Bind("FirstPerson", "Smoothing", 0.3f, "How much the view's position is smoothed as the character moves (0 = locked to the head, 1 = heavy).");
            FpViewDistance = Config.Bind("FirstPerson", "ViewDistance", 60f, "How far you can see in first person, in blocks (shorter = faster).");
            FpCorridor = Config.Bind("FirstPerson", "CorridorWidth", 6f, "Width of the tunnels in first person, in blocks (the character is about 5 blocks tall).");
            FpFaceLights = Config.Bind("FirstPerson", "FaceLights", true, "Turn the game's 2D lights to face you in first person (otherwise they are seen edge-on and light almost nothing).");
            FpLookWithHead = Config.Bind("FirstPerson", "LookWithHead", true, "In first person the character looks where you look (head and torso turn), instead of towards the cursor.");
            FpAimWith = Config.Bind("FirstPerson", "AimWith", "Hand", new ConfigDescription("What points the game's cursor in first person (attacks, mining, clicks): your right hand, or your head (gaze).", new AcceptableValueList<string>("Hand", "Head")));
            FpNoLightShadows = Config.Bind("FirstPerson", "NoLightShadows", true, "Turn off the game's 2D light shadows in first person (seen from inside the play plane they split the view into a dark and a lit half).");
            FpGrabMode = Config.Bind("FirstPerson", "GrabMode", "Hold", new ConfigDescription("First person grip: Hold = keep the grip pressed to keep holding an item; Toggle = press to grab, press again to let go.", new AcceptableValueList<string>("Hold", "Toggle")));
            FpSwingAttack = Config.Bind("FirstPerson", "SwingAttack", true, "In first person a fast swing of the right hand attacks along the swing (the trigger still attacks).");
            FpMinigameTouch = Config.Bind("FirstPerson", "MinigameTouch", true, "In first person, minigames show on a panel within reach and your hand touches it to point (the laser still works from further away).");
            Haptics = Config.Bind("Controllers", "Haptics", true, "Controller vibration for grabs, hits and climbing.");
            HapticStrength = Config.Bind("Controllers", "HapticStrength", 1f, "Vibration strength (0-1).");
            FpBody = Config.Bind("FirstPerson", "Body", true, "In first person, your torso and legs show when you look down, with their wounds, blood, bandages, splints and clothing.");
            FpBodyDepth = Config.Bind("FirstPerson", "BodyThickness", 1f, "How thick your torso and legs are drawn in first person (1 = default).");
            // New key (2026-10-05): the body was too far forward at 0 (user); saved configs get the new default.
            FpBodyBack = Config.Bind("FirstPerson", "BodyBehindEyesMetres", 0.3f, "How far behind your eyes your drawn body sits, in metres (0 = where the game has it, under the eyes).");
            FpMoveSpeed = Config.Bind("FirstPerson", "MoveSpeed", 0.8f, "Your character's top running speed in first person, as a fraction of the game's (1 = the game's own speed; the flat game and the diorama are unchanged).");
            FpArmThickness = Config.Bind("FirstPerson", "ArmThickness", 1f, "How thick your arms are drawn in first person (1 = default, about 13 cm).");
            FpArms = Config.Bind("FirstPerson", "Arms", true, "In first person, the character's own arms (with their wounds, clothing and held item) follow your controllers.");
            FpFaceObjects = Config.Bind("FirstPerson", "FaceObjects", true, "In first person, solid objects in the tunnel (plants, crates, doors, capsules) turn to face you, keeping their thickness.");
            FpBillboards = Config.Bind("FirstPerson", "FaceCharacters", true, "Items, creatures and other characters turn to face you in first person (otherwise they are flat and seen edge-on).");
            FpVoidDarkness = Config.Bind("FirstPerson", "VoidDarkness", 0.6f, "With Void sides: how dark the tunnel gets with distance (0 = none, 1 = black far away).");
            FpBlackout = Config.Bind("FirstPerson", "Blackout", true, "The view goes dark while the character is unconscious or asleep.");
            FpVignette = Config.Bind("FirstPerson", "FallVignette", 0f, "Comfort: darken the edges of the view while falling or jumping fast (0 = off, 1 = strong).");
            FpRagdollView = Config.Bind("FirstPerson", "RagdollView", "Stay", new ConfigDescription("While the body is down (ragdoll, knocked over): stay in first person, or switch to the diorama until it stands up.", new AcceptableValueList<string>("Stay", "Diorama")));
            FpTurning = Config.Bind("FirstPerson", "Turning", "Snap", new ConfigDescription("Right stick sideways: Snap = flick to turn around; Smooth = turn continuously.", new AcceptableValueList<string>("Snap", "Smooth")));
            FpTurnSpeed = Config.Bind("FirstPerson", "SmoothTurnSpeed", 120f, "Degrees per second for smooth turning.");
            // Comfort (docs/FIRSTPERSON_REVIEW.md FP-8; presets in the VR settings' Comfort tab, Intense = these defaults).
            FpAnalogWalk = Config.Bind("FirstPerson", "AnalogWalk", true, "Left stick pushed part way: walk (WalkSpeedMetres); pushed all the way: run (MoveSpeed). Off: any push runs.");
            FpWalkSpeed = Config.Bind("FirstPerson", "WalkSpeedMetres", 1.4f, "Walking speed on a half push of the left stick, in metres per second (a real walk is about 1.4).");
            FpSteadyHeight = Config.Bind("FirstPerson", "SteadyHeight", false, "Keep your eye height steady while standing and running: no idle sway or running bob from the character's animation; steps up and down glide. Jumps and falls still move you.");
            FpSnapBlink = Config.Bind("FirstPerson", "SnapBlink", false, "A short blink (fade) hides the snap turn.");
            FpWallFade = Config.Bind("FirstPerson", "FadeInsideWalls", true, "The view fades to black while your head is inside a block or outside the tunnel's sides (leaning into a wall).");
            // Hands (docs/FIRSTPERSON_REVIEW.md FP-9b/9c/9g; decisions D4, D5).
            FpHandPoses = Config.Bind("FirstPerson", "HandPoses", true, "Your empty hands open, close and point with the controller (the game's own hand art in your character's skin colour). Holding something or wearing something on the hand keeps the character's own hand.");
            FpGrabGlow = Config.Bind("FirstPerson", "GrabGlow", true, "A softly glowing hand marks the item your hand would take (an open hand), or has locked for a pull (a fist). The game's own highlights stay off unless Highlight says otherwise.");
            FpDistancePull = Config.Bind("FirstPerson", "DistancePull", true, "Point at a loose item further off: a glowing hand marks it; grip to lock it, then flick your hand towards you to pull it into your hand. Only items the game would let you pick up (in sight, within its reach of 10 blocks).");
            // Senses and movement (docs/BUILD_PLAN.md Phase 5; FIRSTPERSON_REVIEW FP 10a, 10b, 12a; decision D6).
            FpSoundAtHead = Config.Bind("FirstPerson", "SoundAtHead", true, "In first person you hear from your head: a sound ahead of you is ahead wherever you face, and turning your head turns the sound. Loudness stays the game's.");
            FpRealCrouch = Config.Bind("FirstPerson", "RealCrouch", true, "Crouch for real: lowering your head by CrouchDepthMetres (from where it was at the last recenter) crouches the character. The view follows your own head.");
            FpCrouchDepth = Config.Bind("FirstPerson", "CrouchDepthMetres", 0.3f, "How far your head must drop below its recentered height to crouch (0.15-0.6 m).");
            HapticDamage = Config.Bind("Controllers", "HapticDamage", true, "Both controllers vibrate when you are hurt, stronger for a harder hit (the game's own screen shake and damage).");
            HapticHits = Config.Bind("Controllers", "HapticHits", true, "The right controller vibrates when your attack hits something (not for a swing that misses).");
            HapticHeartbeat = Config.Bind("Controllers", "HapticHeartbeat", false, "When your character is in danger (low blood pressure, oxygen or consciousness, a racing heart), the left controller beats with its heart.");
            FpEyeUnits = Config.Bind("FirstPerson", "MeasuredEyeHeight", 0f, "Internal: the character's standing eye height in blocks, as last measured, so the automatic world scale is right from the first frame (0 = not measured yet).");
            FlipY = Config.Bind("Debug", "FlipY", true, "Flip eye images vertically (Unity D3D render textures are stored bottom-up).");

            foreach (var (control, action) in VrInput.Defaults)
                ControlBinds[control] = Config.Bind("Controls", control, action,
                    new ConfigDescription("Game action for this controller input", new AcceptableValueList<string>(VrInput.Actions)));

            // 2026-10-04: R stick click went favourite -> none (user). Move configs still on the old default once.
            var controlsVersion = Config.Bind("Controls", "MappingVersion", 1, "Internal: default-mapping migrations applied.");
            if (controlsVersion.Value < 2)
            {
                if (ControlBinds["R.stickClick"].Value == "favourite") ControlBinds["R.stickClick"].Value = "none";
                controlsVersion.Value = 2;
            }

            // The game destroys BepInEx's manager object on the first scene load; run on our own.
            new HarmonyLib.Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            var host = new GameObject("CasualtyVR.Core");
            host.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(host);
            var rig = host.AddComponent<VrRig>();
            host.AddComponent<DesktopFirstPerson>();
            host.AddComponent<WindowPlacement>();
            host.AddComponent<VrSettingsMenu>();

            Log.LogInfo($"CasualtyVR {Version}");
            UntestedGameVersion = Application.version != TestedGameVersion;
            if (UntestedGameVersion)
                Log.LogWarning($"Game version {Application.version}: CasualtyVR {Version} was tested with {TestedGameVersion} only; report problems with this version number");
            BridgeLogPath = Path.Combine(Paths.BepInExRootPath, "CasualtyVR-bridge.log");
            try
            {
                string prev = Path.Combine(Paths.BepInExRootPath, "CasualtyVR-bridge.prev.log");
                if (File.Exists(BridgeLogPath)) { if (File.Exists(prev)) File.Delete(prev); File.Move(BridgeLogPath, prev); }
            }
            catch (Exception e) { Log.LogWarning("Could not keep the previous bridge log: " + e.Message); }
            // Headless test (Phase 4a, TEST-027): --cvr-dbg-replay <folder|synthetic> runs the swing and throw recipes over
            // recorded motion takes and logs what they find (tools/motion-takes.ps1).
            {
                var a = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(a, "--cvr-dbg-replay");
                if (i >= 0 && i + 1 < a.Length) try { MotionTakes.Replay(a[i + 1]); } catch (Exception e) { Log.LogError("Motion replay failed: " + e); }
            }
            if (StartInVr.Value || Environment.GetCommandLineArgs().Contains("--cvr-vr"))
                rig.StartVr();
        }
    }
}
