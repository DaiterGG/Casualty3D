using System;
using System.Collections;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal;

namespace CasualtyVR
{
    /// <summary>
    /// Owns the OpenXR session (through CasualtyVR.XrBridge) and two eye cameras that render the game
    /// as a diorama in front of a seated player. The game's own orthographic camera is never moved;
    /// it keeps driving gameplay and the desktop view (it only loses sight of the VR-only frame layer).
    ///
    /// Frame contract (playbook XR-005): LateUpdate waits once and caches the poses, both eye cameras
    /// render from that cache during the normal render loop, and the submit event is issued at end of
    /// frame so it runs after both eyes. The bridge submits exactly the views it handed out.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    internal sealed class VrRig : MonoBehaviour
    {
        // XrSessionState values we care about.
        private const int StateFocused = 5;
        // Frames of FOCUSED tracking before the seated origin is captured automatically. The first poses a
        // runtime reports can be placeholders: VirtualDesktopXR reported the head at y=-1.27 m before
        // tracking started, which put the diorama ~1.3 m too low (user report 2026-10-03).
        private const int SettleFrames = 45;
        private const int ShutdownTimeoutMs = 3000;

        public static VrRig Instance { get; private set; }

        public bool Active { get; private set; }

        private XrNative.Info info;
        private readonly Camera[] eyes = new Camera[2];
        private VrPostOverride postOverride;
        private int eyeMask;
        private string lastProfile = "";
        private bool theatreWas;
        private ParticleAlignment particles;
        private readonly RenderTexture[] targets = new RenderTexture[2];
        private DioramaFrame frame;
        private Greenscreen greenscreen;
        private VoxelTerrain voxels;
        private SpriteSlabs slabs;
        private ObjectExtruder extruder;
        private LiquidVolume liquids;
        private VrRain rain;
        private WorldText worldText;
        private Ears ears;
        private float dbgTalkAt = 16f;
        private float nextSpeedLog;
        private static readonly float DbgFpOffAt = float.TryParse(ArgValue("--cvr-dbg-fpoff"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fpOff) ? fpOff : -1f;
        private static string ArgValue(string flag) { var a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, flag); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        private static readonly bool DbgTalk = Array.IndexOf(Environment.GetCommandLineArgs(), "--cvr-dbg-talk") >= 0;
        private FirstPerson firstPerson;
        private VrUi ui;
        private IntPtr submitEvent;
        private bool waitedThisFrame;
        private int savedVSync;
        private Camera copiedFrom;
        private ulong lastFrameIndex;
        private bool bridgeWedged;

        // Seated origin: follows the head until the session has settled, then stays put until F9.
        private bool anchored;
        private bool manualRecenter;
        private bool runtimeRecenter;
        private int focusedFrames;
        private Vector3 seatedOrigin;
        private bool pacedByRuntime;
        private int savedTargetFps = -1;
        private bool versionNoticeShown;
        private static readonly float DbgRecenterAt = float.TryParse(ArgValue("--cvr-dbg-recenter"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rc) ? rc : -1f;
        private bool dbgRecentered;
        private static readonly float DbgThrowAt = float.TryParse(ArgValue("--cvr-dbg-throw"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float th) ? th : -1f;

        // Debug switches for isolating cost (command line): --cvr-dbg-noeyes, --cvr-dbg-nopopup, --cvr-dbg-nopost
        private static readonly string[] Args = Environment.GetCommandLineArgs();
        private static readonly bool DbgNoEyes = Array.IndexOf(Args, "--cvr-dbg-noeyes") >= 0;
        private static readonly bool DbgNoPopup = Array.IndexOf(Args, "--cvr-dbg-nopopup") >= 0;
        private static readonly bool DbgNoPost = Array.IndexOf(Args, "--cvr-dbg-nopost") >= 0;
        private static readonly bool DbgNoVoxels = Array.IndexOf(Args, "--cvr-dbg-novoxels") >= 0;
        private static readonly bool DbgNoSlabs = Array.IndexOf(Args, "--cvr-dbg-noslabs") >= 0;
        internal static readonly bool DbgNoLiquids = Array.IndexOf(Args, "--cvr-dbg-noliquids") >= 0;
        private static readonly bool DbgFog = Array.IndexOf(Args, "--cvr-dbg-fog") >= 0;
        private static readonly bool DbgDumpRenderers = Array.IndexOf(Args, "--cvr-dbg-dumprenderers") >= 0;
        private bool dumpedRenderers;
        private static readonly bool DbgCave = Array.IndexOf(Args, "--cvr-dbg-cave") >= 0;
        private bool caveDone;
        private static readonly string DbgModifiers = ArgAfter("--cvr-dbg-modifier");
        private bool modifiersDone;
        private static readonly string DbgSpawn = ArgAfter("--cvr-dbg-spawn");
        private static readonly bool DbgRope = Array.IndexOf(Args, "--cvr-dbg-rope") >= 0;
        private static readonly float DbgHurtAt = float.TryParse(ArgAfter("--cvr-dbg-hurt"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float hurtAt) ? hurtAt : 0f;
        private bool roped, dbgHurt;
        private bool spawned;
        private static string ArgAfter(string f) { int i = Array.IndexOf(Args, f); return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null; }
        internal static readonly bool DbgNoFog = Array.IndexOf(Args, "--cvr-dbg-nofog") >= 0;

        private float nextLogPump;
        private int statFrames;
        // Frame-time distribution per log window (BUILD_PLAN Phase 0, IP 7/8): averages hide hitches.
        private readonly System.Collections.Generic.List<float> frameMs = new System.Collections.Generic.List<float>(256);
        private readonly System.Collections.Generic.List<int> slowFrames = new System.Collections.Generic.List<int>();
        private float periodMs = 1000f / 90f;
        private float statWorst;

        private void Awake()
        {
            Instance = this;
            StartCoroutine(EndOfFrameSubmit());
            UnityEngine.Rendering.RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            int i = Array.IndexOf(Args, "--cvr-dbg-cycle");
            if (i >= 0 && i + 1 < Args.Length) StartCoroutine(CycleTest(int.Parse(Args[i + 1])));
        }

        /// <summary>XR-004 proof: stop/start VR repeatedly once focused; every cycle must reach FOCUSED again.</summary>
        private IEnumerator CycleTest(int cycles)
        {
            for (int c = 1; c <= cycles; c++)
            {
                float deadline = Time.realtimeSinceStartup + 30f;
                while (!(Active && info.sessionState == StateFocused && anchored) && Time.realtimeSinceStartup < deadline)
                    yield return null;
                bool ok = Active && info.sessionState == StateFocused;
                Plugin.Log.LogInfo($"cycle {c}/{cycles}: {(ok ? "FOCUSED and anchored" : "did NOT reach FOCUSED")} -> stopping");
                yield return new WaitForSecondsRealtime(1f);
                StopVr("cycle test");
                yield return new WaitForSecondsRealtime(1f);
                StartVr();
            }
            Plugin.Log.LogInfo($"cycle test finished ({cycles} cycles); VR left running");
        }

        private void OnDestroy()
        {
            if (Active) StopVr("rig destroyed");
        }

        private void OnApplicationQuit()
        {
            if (Active) StopVr("application quit");
        }

        private void Update()
        {
            if (Input.GetKeyDown(Plugin.ToggleKey.Value))
            {
                if (Active) StopVr("toggle key");
                else StartVr();
            }
            if (!Active) return;

            if (Input.GetKeyDown(Plugin.RecenterKey.Value)) manualRecenter = true;
            if (Input.GetKeyDown(Plugin.ZoomInKey.Value)) Zoom(1f / 1.1f);
            if (Input.GetKeyDown(Plugin.ZoomOutKey.Value)) Zoom(1.1f);
            if (Input.GetKeyDown(Plugin.DashboardKey.Value) && ui != null) ui.ForceDashboard = !ui.ForceDashboard;
            if (Input.GetKeyDown(Plugin.FpKey.Value))
            {
                bool now = !Plugin.FpWanted;
                Plugin.FpForced = false;
                Plugin.FpOn.Value = now;
            }
            if (Input.GetKeyDown(Plugin.VoxelKey.Value))
            {
                Plugin.VoxelsOn.Value = !Plugin.VoxelsOn.Value;
                Plugin.Log.LogInfo($"Voxels: {(Plugin.VoxelsOn.Value ? "on" : "off")}");
            }

            XrNative.cvr_xr_poll();
            XrNative.cvr_xr_get_info(out info);
            // The runtime ended the session (headset disconnected, Virtual Desktop or SteamVR closed): nothing will
            // pace or show our frames again, so stop cleanly instead of failing every frame (FAIL-XR-029).
            if (info.sessionLost != 0)
            {
                StopVr("the OpenXR runtime ended the session");
                ScreenNotice.Show($"CasualtyVR: the VR session ended (the headset disconnected or its PC app closed). Press {Plugin.ToggleKey.Value} to start VR again.");
                return;
            }
            // The runtime recentred LOCAL (e.g. a long press of the Quest button): re-anchor on the next tracked frame.
            if (info.referenceSpaceChanged != 0) runtimeRecenter = true;
            UpdatePacing();
            LogPerf();

            var main = Camera.main;
            if (main != null && main != copiedFrom && !IsEye(main)) SyncEyesFrom(main);
        }

        /// <summary>
        /// While the session runs, xrWaitFrame paces the game: monitor vsync and the game's own frame cap (its
        /// console "fps" command) would be a second scheduler (FAIL-XR-024). While it does not (headset asleep, the
        /// session stopping), the game's own settings come back, so it does not run uncapped.
        /// </summary>
        private void UpdatePacing()
        {
            bool paced = info.sessionRunning != 0 && info.sessionLost == 0;
            if (paced)
            {
                if (!pacedByRuntime) Plugin.Log.LogInfo("Frame pacing: the OpenXR runtime (vsync and frame cap off)");
                if (QualitySettings.vSyncCount != 0) { savedVSync = QualitySettings.vSyncCount; QualitySettings.vSyncCount = 0; }
                if (Application.targetFrameRate != -1) { savedTargetFps = Application.targetFrameRate; Application.targetFrameRate = -1; }
            }
            else if (pacedByRuntime) RestorePacing("the session is not running");
            pacedByRuntime = paced;
        }

        private void RestorePacing(string why)
        {
            QualitySettings.vSyncCount = savedVSync;
            Application.targetFrameRate = savedTargetFps;
            pacedByRuntime = false;
            Plugin.Log.LogInfo($"Frame pacing: the game's own ({why}; vsync {savedVSync}, frame cap {savedTargetFps})");
        }

        // Per-feature fault isolation (FAIL-TEST-031): an exception in one feature (for example after a game update
        // renamed something it uses) turns off that feature for the session instead of aborting the whole VR frame
        // every frame. The view, the submit and the controls keep working.
        private readonly System.Collections.Generic.HashSet<string> failedFeatures = new System.Collections.Generic.HashSet<string>();
        private bool On(string feature) => !failedFeatures.Contains(feature);

        private void Fail(string feature, Exception e)
        {
            if (!failedFeatures.Add(feature)) return;
            Plugin.Log.LogError($"VR feature '{feature}' failed and is off until VR restarts: {e}");
            ScreenNotice.Show($"CasualtyVR: '{feature}' stopped working and was turned off (details in BepInEx\\LogOutput.log). VR keeps running.");
            if (feature == "first person" && firstPerson != null)
            {
                try { firstPerson.Destroy(); } catch (Exception de) { Plugin.Log.LogError("First person cleanup failed: " + de); }
                firstPerson = null;
            }
            if (feature == "sound at head" && ears != null)
            {
                try { ears.Release("the feature failed"); } catch (Exception de) { Plugin.Log.LogError("Ears cleanup failed: " + de); }
            }
        }

        private void LateUpdate()
        {
            if (!Active) return;
            DesktopFirstPerson.SuspendForVr();
            bool render = false, inputDone = false;
            if (info.sessionRunning != 0 && XrNative.cvr_xr_wait_frame(out var xrFrame) == 1)
            {
                waitedThisFrame = true;
                if (lastFrameIndex != 0 && xrFrame.frameIndex != lastFrameIndex + 1)
                    Plugin.Log.LogWarning($"Frame index jumped {lastFrameIndex} -> {xrFrame.frameIndex}");
                lastFrameIndex = xrFrame.frameIndex;
                if (xrFrame.predictedDisplayPeriod > 0) periodMs = xrFrame.predictedDisplayPeriod / 1e6f;
                if (xrFrame.shouldRender != 0 && xrFrame.viewsValid != 0)
                {
                    UpdateSeatedOrigin(xrFrame);
                    Vector3 focus = Focus();
                    // One tracking->world mapping for this frame (eyes, controllers, UI): first person or diorama.
                    bool fp = false;
                    if (firstPerson != null && On("first person"))
                    {
                        FirstPerson.UpdateTime.Start();
                        try { fp = firstPerson.Update(ui.TheatreShown, seatedOrigin); }
                        catch (Exception e) { fp = false; Fail("first person", e); }
                        FirstPerson.UpdateTime.Stop();
                    }
                    if (!fp) VrSpace.SetDiorama(focus, seatedOrigin);
                    // Both eyes' average: on canted displays (Index, Pimax) each eye is turned a few degrees outwards.
                    VrSpace.SetHead((ToUnity(xrFrame.left.position) + ToUnity(xrFrame.right.position)) * 0.5f, Quaternion.Slerp(VrSpace.ToUnity(xrFrame.left.orientation), VrSpace.ToUnity(xrFrame.right.orientation), 0.5f));
                    if (fp && firstPerson != null)
                    {
                        FirstPerson.UpdateTime.Start();
                        try { firstPerson.AfterHead(ui.DashboardShown); } catch (Exception e) { Fail("first person", e); }
                        FirstPerson.UpdateTime.Stop();
                    }
                    // Phase 5: the ears at the head in first person; vibration from the game's own events.
                    if (On("sound at head")) try { ears?.Update(fp); } catch (Exception e) { Fail("sound at head", e); }
                    if (On("game haptics")) try { GameHaptics.Update(); } catch (Exception e) { Fail("game haptics", e); }
                    if (On("frame")) try { frame.Update(LogicCamera(), focus); } catch (Exception e) { Fail("frame", e); }
                    if (On("greenscreen")) try { greenscreen.Update(eyes, copiedFrom); postOverride.SetGreenscreen(Plugin.GreenscreenOn); } catch (Exception e) { Fail("greenscreen", e); }
                    if (On("particles")) try { particles.Update(fp); } catch (Exception e) { Fail("particles", e); }
                    if (!DbgNoVoxels && On("3D blocks")) try { voxels.Update(); } catch (Exception e) { Fail("3D blocks", e); }
                    if (On("object depth")) try { extruder.Update(); } catch (Exception e) { Fail("object depth", e); }
                    if (!DbgNoSlabs && On("character thickness")) try { slabs.Update(); } catch (Exception e) { Fail("character thickness", e); }
                    if (On("debug hooks")) try { DebugHooks(); } catch (Exception e) { Fail("debug hooks", e); }
                    if (On("liquids")) try { liquids.Update(); } catch (Exception e) { Fail("liquids", e); }
                    if (On("rain")) try { rain.Update(); } catch (Exception e) { Fail("rain", e); }
                    if (On("world text")) try { worldText.Update(); } catch (Exception e) { Fail("world text", e); }
                    if (On("minigame hands")) try { VrHands.UpdateMinigamePivot(true); } catch (Exception e) { Fail("minigame hands", e); }
                    if (On("HUD scale")) try { HudScaler.Apply(true); } catch (Exception e) { Fail("HUD scale", e); }
                    if (On("menus")) try { ui.Update(focus, LogicCamera(), seatedOrigin, Plugin.UnitsPerMetre.Value); } catch (Exception e) { Fail("menus", e); }
                    XrNative.cvr_xr_get_input(out var input);
                    if (On("controls")) try { VrInput.Update(input, ui, focus, seatedOrigin, Plugin.UnitsPerMetre.Value); } catch (Exception e) { Fail("controls", e); }
                    inputDone = true;
                    if (firstPerson != null && On("first person"))
                    {
                        FirstPerson.UpdateTime.Start();
                        try { firstPerson.AfterInput(); } catch (Exception e) { Fail("first person", e); }
                        FirstPerson.UpdateTime.Stop();
                    }
                    if (input.profile != lastProfile) { lastProfile = input.profile; Plugin.Log.LogInfo($"Controllers: profile '{input.profile}'"); }
                    // Theatre (menu, loading, end screen): nothing of the level behind the panel.
                    int mask = ui.TheatreShown ? (1 << DioramaFrame.Layer) : fp ? eyeMask & ~(1 << DioramaFrame.Layer) : eyeMask;
                    foreach (var eye in eyes) if (eye.cullingMask != mask) eye.cullingMask = mask;
                    if (ui.TheatreShown != theatreWas)
                    {
                        // Behind a theatre panel the game's screen effects (e.g. the dying wash) are just haze.
                        foreach (var eye in eyes)
                        {
                            if (ui.TheatreShown) ScreenPassFilter.Exclude(eye);
                            else ScreenPassFilter.Exclude(eye, "AberPass");
                        }
                        theatreWas = ui.TheatreShown;
                    }
                    ApplyView(eyes[0], xrFrame.left);
                    ApplyView(eyes[1], xrFrame.right);
                    render = true;
                }
            }
            // Controls run every frame, rendered or not (FAIL-INPUT-025): when the runtime stops rendering (headset
            // off, its menu), tracking is lost or this frame skipped its wait, held keys must still release, and a
            // key edge must not repeat. With no running session the input is neutral.
            if (!inputDone && On("controls"))
            {
                var input = default(XrNative.Input);
                if (info.sessionRunning != 0) XrNative.cvr_xr_get_input(out input);
                try { VrInput.Update(input, ui, Focus(), seatedOrigin, Plugin.UnitsPerMetre.Value, posesFresh: false); }
                catch (Exception e) { Fail("controls", e); }
            }
            // The laser's mouse buttons reach mods' OnGUI windows too.
            if (On("mod windows")) try { ImguiCapture.QueueClicks(); } catch (Exception e) { Fail("mod windows", e); }
            foreach (var eye in eyes)
                if (eye) eye.enabled = render && !DbgNoEyes;
        }

        // --cvr-dbg-clone: a copy of the player's character a few blocks to the right, as the co-op mod makes other
        // players (it instantiates the local character), with test labels on it.
        private bool cloned;

        private void CloneTestCharacter()
        {
            cloned = true;
            var body = PlayerCamera.main.body;
            var chara = body.transform.parent != null ? body.transform.parent.gameObject : body.gameObject;
            var copy = Instantiate(chara, chara.transform.position + new Vector3(6f, 0f, 0f), chara.transform.rotation);
            copy.name = "CasualtyVR.TestCharacter";
            var cb = copy.GetComponentInChildren<Body>();
            Plugin.Log.LogInfo($"Clone test: '{chara.name}' copied, body {(cb != null ? cb.name : "none")} at {(cb != null ? cb.transform.position.ToString() : "-")}");
            if (cb != null) AttachTestLabels(cb);
        }

        private static void AttachTestLabels(Body body)
        {
            var root = body.transform.parent;
            Plugin.Log.LogInfo($"Attach test: body '{body.name}' root '{(root != null ? root.name : "none")}' children {(root != null ? root.childCount : 0)} head {body.limbs[0].transform.position}");
            if (root == null) return;
            var prefab = Resources.Load("special/ExpTalkText") as GameObject;
            if (prefab != null)
            {
                var tag = Instantiate(prefab, root);
                tag.SetActive(true);
                tag.name = "CharacterNametag_Test";
                var tmp = tag.GetComponent<TMPro.TextMeshPro>();
                if (tmp != null) { tmp.text = "Test player"; tmp.color = new Color(0.4f, 1f, 0.6f, 0.87f); }
                tag.transform.position = (Vector2)body.transform.position - Vector2.up * 3f;
            }
            var icon = new GameObject("KrokoshaMP_CharacterStatusIcon_Test");
            icon.transform.SetParent(root, false);
            var sr = icon.AddComponent<SpriteRenderer>();
            sr.sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            sr.color = new Color(1f, 0.6f, 0.1f, 0.87f);
            sr.sortingOrder = 6000;
            icon.transform.position = (Vector2)body.limbs[0].transform.position + Vector2.up * 2f;
            Plugin.Log.LogInfo($"Attach test: tag at {(prefab != null ? root.Find("CharacterNametag_Test").position.ToString() : "-")}, icon at {icon.transform.position}");
        }

        /// <summary>Headless test hooks (command-line flags only; nothing here runs in a normal game).</summary>
        private void DebugHooks()
        {
            // --cvr-dbg-recenter <s>: the F9 recenter at that level time (heading test with xr-sim "head rot").
            if (DbgRecenterAt > 0f && !dbgRecentered && Time.timeSinceLevelLoad > DbgRecenterAt && PlayerCamera.main != null)
            {
                dbgRecentered = true;
                manualRecenter = true;
                Plugin.Log.LogInfo("Recenter test: requested");
            }
            // --cvr-dbg-rope (tools/fp-comfort.ps1, FP 12b): a climbing rope half a block ahead of the player, hung the way the
            // game's climbing rope item hangs one: from up to 8 blocks above (under any ceiling) down to the ground.
            if (!roped && DbgRope && Time.timeSinceLevelLoad > 14f && PlayerCamera.main != null && PlayerCamera.main.body != null
                && WorldGeneration.world != null && !WorldGeneration.world.generatingWorld)
            {
                roped = true;
                var rb = PlayerCamera.main.body;
                Vector2 at = (Vector2)rb.transform.position + new Vector2(rb.isRight ? 0.5f : -0.5f, 0f);
                int ground = LayerMask.GetMask("Ground");
                var up = Physics2D.Linecast(at, at + Vector2.up * 8f, ground);
                Vector2 top = up ? up.point - Vector2.up * 0.2f : at + Vector2.up * 8f;
                var down = Physics2D.Linecast(top, top + Vector2.down * 25f, ground);
                Vector2 bottom = down ? down.point : top + Vector2.down * 25f;
                var rope = Utils.Create("climbingropeextended", top, 0f);
                var climbable = rope.GetComponent<Climbable>();
                climbable.points.Add(bottom);
                climbable.points.Add(top);
                rope.transform.GetChild(0).localScale = new Vector2(1f, Vector2.Distance(top, bottom) / 25f);
                Plugin.Log.LogInfo($"Rope test: hung from {top} to {bottom} ({Vector2.Distance(top, bottom):F1} blocks)");
            }
            // --cvr-dbg-hurt <s> (FP 10b): the back arm dislocates at that level time (the game's own pain and screen shake).
            if (DbgHurtAt > 0f && !dbgHurt && Time.timeSinceLevelLoad > DbgHurtAt && PlayerCamera.main != null && PlayerCamera.main.body != null)
            {
                dbgHurt = true;
                var limb = PlayerCamera.main.body.limbs[6];
                limb.Dislocate();
                Plugin.Log.LogInfo($"Hurt test: dislocated '{limb.name}'");
            }
            if (!cloned && Array.IndexOf(Args, "--cvr-dbg-clone") >= 0 && Time.timeSinceLevelLoad > 16f && PlayerCamera.main != null && PlayerCamera.main.body != null
                && WorldGeneration.world != null && !WorldGeneration.world.generatingWorld)
                CloneTestCharacter();
            // --cvr-dbg-throw <s>: this hook throws at that level time (fault-isolation test: VR must keep running).
            if (DbgThrowAt > 0f && Time.timeSinceLevelLoad > DbgThrowAt)
                throw new InvalidOperationException("--cvr-dbg-throw test exception");
            if (DbgSpawn != null && !spawned && Time.timeSinceLevelLoad > 14f && PlayerCamera.main != null && PlayerCamera.main.body != null)
            // Headless test objects: --cvr-dbg-spawn <Resources path>[,<path>...], 6, 9, 12... blocks right of the player
            // (after --cvr-dbg-cave's teleport at 12 s).
            foreach (var (spawnPath, spawnIndex) in DbgSpawn.Split(',').Select((p, i) => (p, i)))
            {
                spawned = true;
                var res = Resources.Load(spawnPath) as GameObject;
                if (res == null) Plugin.Log.LogWarning($"Spawn: '{spawnPath}' not found");
                else
                {
                    var go = (GameObject)Instantiate(res, PlayerCamera.main.body.transform.position + new Vector3((Array.IndexOf(Args, "--cvr-dbg-spawn-near") >= 0 ? 0.5f : 2.5f) + 1.5f * spawnIndex, 1f, 0f), Quaternion.identity);
                    // --cvr-dbg-attach: a co-op-mod-style name tag and status icon on the spawned character (e.g. corpse),
                    // as Casualties Together makes them: siblings of the Body under its root (WorldText test).
                    if (Array.IndexOf(Args, "--cvr-dbg-attach") >= 0 && go.GetComponentInChildren<Body>() is Body tb)
                        AttachTestLabels(tb);
                    // --cvr-dbg-hold: the first spawned item goes into the character's active hand.
                    if (spawnIndex == 0 && Array.IndexOf(Args, "--cvr-dbg-hold") >= 0 && go.TryGetComponent<Item>(out var heldItem))
                    {
                        var hb = PlayerCamera.main.body;
                        hb.PickUpItem(heldItem, hb.handSlot, force: true);
                        Plugin.Log.LogInfo($"Spawn: '{spawnPath}' put in hand slot {hb.handSlot}: {hb.HoldingItem(hb.handSlot)}");
                    }
                    // --cvr-dbg-keypad: open the spawned object's keypad minigame (headless minigame test).
                    if (Array.IndexOf(Args, "--cvr-dbg-keypad") >= 0 && go.TryGetComponent<BuildingEntity>(out var be))
                        MinigameBase.main.StartMinigame(new KeypadMinigame(be, KeypadMinigame.GenerateCode()), null);
                    // --cvr-dbg-lockpick: the lockpicking minigame on it, by hand (no kit), 5 degrees either way.
                    if (Array.IndexOf(Args, "--cvr-dbg-lockpick") >= 0 && go.TryGetComponent<BuildingEntity>(out var lbe))
                        MinigameBase.main.StartMinigame(new LockpingMinigame(lbe, 5f), null);
                    foreach (var c in go.GetComponentsInChildren<Component>(true))
                    {
                        string extra = c is SpriteRenderer sr ? $" sprite {sr.sprite?.name} {sr.sortingLayerName}/{sr.sortingOrder} size {sr.bounds.size} drawMode {sr.drawMode} shader {sr.sharedMaterial?.shader?.name} layer {sr.gameObject.layer}"
                                     : c is Collider2D col ? $" trigger {col.isTrigger}" : "";
                        Plugin.Log.LogInfo($"Spawn: {c.gameObject.name} : {c.GetType().Name}{extra}");
                    }
                }
            }
            if (DbgModifiers != null && !modifiersDone && Time.timeSinceLevelLoad > 10f && WorldGeneration.world != null && !WorldGeneration.world.generatingWorld && PlayerCamera.main != null && PlayerCamera.main.body != null)
            {
                // --cvr-dbg-modifier <index>[,<index>]: apply layer modifiers as WorldGeneration.ApplyLayerModifiers
                // does (0 Foggy, 1 Warm, 2 Hot, 3 Cold, 4 Freezing, 5 Flooded, 6 Dry, 7 Rainy, 8 Infested, 9 Ionized, 10 Unchipped).
                modifiersDone = true;
                foreach (var part in DbgModifiers.Split(','))
                {
                    if (!int.TryParse(part, out int mi) || mi < 0 || mi >= LayerModifier.availableModifiers.Length) continue;
                    var mod = LayerModifier.availableModifiers[mi];
                    if (mod.active) continue;
                    mod.Initialize(WorldGeneration.world);
                    mod.active = true;
                    Plugin.Log.LogInfo($"Modifier test: applied {mod.GetType().Name} ({mi})");
                    var rs = WorldGeneration.world.rainSprite;
                    if (mi == 7 && rs != null)
                    {
                        string path = rs.name;
                        for (var tt = rs.transform.parent; tt != null; tt = tt.parent) path = tt.name + "/" + path;
                        Plugin.Log.LogInfo($"Modifier test: rainSprite {path} sprite {rs.sprite?.name} {rs.sortingLayerName}/{rs.sortingOrder} drawMode {rs.drawMode} size {rs.bounds.size} pos {rs.transform.position} shader {rs.sharedMaterial?.shader?.name} layer {rs.gameObject.layer}");
                    }
                }
            }
            // --cvr-dbg-rain: the rain overlay at full strength (the game sets its alpha from a rain map each frame).
            if (Array.IndexOf(Args, "--cvr-dbg-rain") >= 0 && WorldGeneration.world != null && WorldGeneration.world.rainSprite != null)
                WorldGeneration.world.rainSprite.color = Color.white;
            if (DbgCave && !caveDone && Time.timeSinceLevelLoad > 12f && PlayerCamera.main != null && PlayerCamera.main.body != null && WorldGeneration.world != null && !WorldGeneration.world.generatingWorld)
            {
                // --cvr-dbg-cave: headless first-person test spot; teleport to the nearest open tunnel
                // (14 x 8 blocks of air over a solid floor) below the start.
                caveDone = true;
                var w = WorldGeneration.world;
                var start = w.WorldToBlockPos(PlayerCamera.main.body.transform.position);
                bool Air(int x, int y) => w.GetBlock(new Vector2Int(x, y)) == 0;
                Vector2Int? found = null;
                for (int dy = 12; dy < 200 && found == null; dy++)
                    for (int dx = 0; dx < 160 && found == null; dx += 2)
                        foreach (int sx in new[] { start.x + dx, start.x - dx })
                        {
                            int y0 = start.y - dy;
                            if (y0 < 2 || sx < 8 || sx > w.width - 8) continue;
                            if (Air(sx, y0 - 1)) continue;   // floor
                            bool ok = true;
                            for (int x = sx - 7; x <= sx + 7 && ok; x++)
                                for (int y = y0; y < y0 + 8 && ok; y++) ok = Air(x, y);
                            if (ok) { found = new Vector2Int(sx, y0); break; }
                        }
                if (found.HasValue)
                {
                    Vector2 pos = w.BlockToWorldPos(found.Value) + new Vector2(0f, 2.6f);
                    PlayerCamera.main.body.transform.position = pos;
                    PlayerCamera.main.transform.position = new Vector3(pos.x, pos.y, PlayerCamera.main.transform.position.z);
                    Plugin.Log.LogInfo($"Cave test: teleported to {pos}");
                }
                else Plugin.Log.LogWarning("Cave test: no open tunnel found");
            }
            if (DbgDumpRenderers && !dumpedRenderers && Time.timeSinceLevelLoad > 15f && PlayerCamera.main != null && PlayerCamera.main.body != null)
            {
                // Every renderer within 12 units of the player: what PopupDepth does with it (offset glow hunt).
                dumpedRenderers = true;
                var p = PlayerCamera.main.body.transform.position;
                foreach (var r in FindObjectsOfType<Renderer>())
                {
                    if (!r.enabled || ((Vector2)(r.bounds.center - p)).magnitude > 12f || r.gameObject.layer >= 25) continue;
                    string path = r.name;
                    for (var t = r.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                    Plugin.Log.LogInfo($"R: {path} | {r.GetType().Name} {r.sortingLayerName}/{r.sortingOrder} depth {PopupDepth.DepthFor(r.sortingLayerName, r.sortingOrder):F2} col {(r.GetComponent<Collider2D>() != null)} layer {r.gameObject.layer} shader {(r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-")} z {r.transform.position.z:F2}");
                }
            }
            if (DbgFog && WorldGeneration.world != null && !WorldGeneration.world.generatingWorld && WorldGeneration.world.fogAmount < 1f)
            {
                WorldGeneration.world.SetFog(1f);   // headless test of the Foggy modifier
                Plugin.Log.LogInfo($"Fog: forced on (sprite '{WorldGeneration.world.fogSprite.name}' sorting {WorldGeneration.world.fogSprite.sortingLayerName}/{WorldGeneration.world.fogSprite.sortingOrder} pos {WorldGeneration.world.fogSprite.transform.position} parent {WorldGeneration.world.fogSprite.transform.parent?.name} mat {WorldGeneration.world.fogMat?.shader?.name})");
            }
            // --cvr-dbg-fpoff <s>: first person off at that level time (speed-restore test); the cap is logged.
            if (DbgFpOffAt > 0f && PlayerCamera.main != null && PlayerCamera.main.body != null)
            {
                var sb = PlayerCamera.main.body;
                if (Time.timeSinceLevelLoad > DbgFpOffAt && Plugin.FpWanted) { Plugin.FpForced = false; Plugin.FpOn.Value = false; nextSpeedLog = 0f; }
                if (Time.timeSinceLevelLoad > DbgFpOffAt - 3f && Time.unscaledTime >= nextSpeedLog)
                {
                    nextSpeedLog = Time.unscaledTime + 1f;
                    Plugin.Log.LogInfo($"Speed check: first person {FirstPerson.Active} cap {sb.actualMaxSpeed:F1} of {sb.maxSpeed:F1}");
                }
            }
            // --cvr-dbg-talk: the character speaks every 6 s (world-text test).
            if (DbgTalk && Time.timeSinceLevelLoad > dbgTalkAt && PlayerCamera.main != null && PlayerCamera.main.body != null && PlayerCamera.main.body.TryGetComponent<Talker>(out var talker))
            {
                dbgTalkAt = Time.timeSinceLevelLoad + 6f;
                talker.Talk("Test line: where am I? It is dark down here.", null, true, true);
                WorldGeneration.CreateDamageNumber((Vector2)PlayerCamera.main.body.transform.position + new Vector2(PlayerCamera.main.body.isRight ? 6f : -6f, 2f), 42);
                Plugin.Log.LogInfo("Talk test: line spoken, damage number shown");
            }
        }

        private IEnumerator EndOfFrameSubmit()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                if (Active) try { ImguiCapture.EndOfFrame(); } catch (Exception e) { Plugin.Log.LogError("IMGUI capture: " + e); }
                if (Active && waitedThisFrame)
                {
                    waitedThisFrame = false;
                    GL.IssuePluginEvent(submitEvent, 1);
                }
            }
        }

        public void StartVr()
        {
            if (Active) return;
            if (bridgeWedged)
            {
                Plugin.Log.LogError("VR start refused: a previous shutdown did not finish; restart the game");
                ScreenNotice.Show("CasualtyVR: VR cannot restart in this session. Restart the game.");
                return;
            }
            // The bridge takes Unity's texture as a D3D11 one: any other API (-force-vulkan, -force-d3d12) would crash it.
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
            {
                Plugin.Log.LogError($"VR start failed: the game renders with {SystemInfo.graphicsDeviceType}; CasualtyVR needs Direct3D 11");
                ScreenNotice.Show($"CasualtyVR: VR needs Direct3D 11, but the game is using {SystemInfo.graphicsDeviceType}. Remove -force-vulkan / -force-d3d12 from the launch options.");
                return;
            }
            if (!XrNative.Preload(Plugin.NativeDir, out string preloadError))
            {
                Plugin.Log.LogError("VR start failed: " + preloadError);
                ScreenNotice.Show("CasualtyVR: VR could not start (the mod's native files did not load). Reinstall the mod; details in BepInEx\\LogOutput.log.");
                return;
            }
            // The bridge's own log on disk, flushed per line: survives a crash or a runtime hang (FAIL-TEST-039).
            try { XrNative.cvr_xr_set_log_file(Plugin.BridgeLogPath); }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge log file unavailable: " + e.Message); }
            if (Plugin.UntestedGameVersion && !versionNoticeShown)
            {
                versionNoticeShown = true;
                ScreenNotice.Show($"CasualtyVR {Plugin.Version} was tested with Casualties: Unknown {Plugin.TestedGameVersion}; this is {Application.version}. Some VR features may not work.", 8f);
            }

            // The bridge finds Unity's D3D11 device through any Unity-created texture.
            var probe = new RenderTexture(4, 4, 0, GraphicsFormat.R8G8B8A8_SRGB);
            probe.Create();
            var error = new StringBuilder(512);
            int ok = XrNative.cvr_xr_init(probe.GetNativeTexturePtr(), error, error.Capacity);
            probe.Release();
            Destroy(probe);
            PumpNativeLog();
            if (ok != 1)
            {
                Plugin.Log.LogError("VR start failed: " + error);
                string reason = error.ToString();
                string key = Plugin.ToggleKey.Value.ToString();
                ScreenNotice.Show(reason.Contains("xrCreateInstance")
                    ? $"CasualtyVR: no OpenXR runtime is available. Start your headset's PC app (Virtual Desktop, SteamVR or Meta Quest Link), make it the active OpenXR runtime, then press {key}."
                    : reason.Contains("no head-mounted display")
                        ? $"CasualtyVR: the OpenXR runtime sees no headset. Connect the headset, then press {key}."
                        : $"CasualtyVR: VR could not start: {reason}.\nDetails in BepInEx\\LogOutput.log.");
                return;
            }

            XrNative.cvr_xr_get_info(out info);
            Plugin.Log.LogInfo($"OpenXR: runtime '{info.runtimeName}' system '{info.systemName}' api {info.apiMajor}.{info.apiMinor}, eye {info.recommendedWidth}x{info.recommendedHeight}, swapchain format {info.swapchainFormat}");

            for (int i = 0; i < 2; i++)
            {
                // The bridge picks RGBA8 when the runtime has it, else BGRA8 (DXGI 87 and 91); CopyResource needs the same family.
                var eyeFormat = info.swapchainFormat == 87 || info.swapchainFormat == 91 ? GraphicsFormat.B8G8R8A8_SRGB : GraphicsFormat.R8G8B8A8_SRGB;
                var desc = new RenderTextureDescriptor(info.recommendedWidth, info.recommendedHeight, eyeFormat, 24)
                {
                    msaaSamples = 1,
                    useMipMap = false,
                };
                targets[i] = new RenderTexture(desc) { name = i == 0 ? "CasualtyVR.EyeL" : "CasualtyVR.EyeR" };
                targets[i].Create();

                var go = new GameObject(i == 0 ? "CasualtyVR.EyeL" : "CasualtyVR.EyeR") { hideFlags = HideFlags.DontSave };
                DontDestroyOnLoad(go);
                eyes[i] = go.AddComponent<Camera>();
                eyes[i].enabled = false;
                if (!DbgNoPopup) PopupDepth.Register(eyes[i]);
                // Screen-locked colour fringing in a headset: keep the game's damage/consciousness passes, drop this one.
                ScreenPassFilter.Exclude(eyes[i], "AberPass");

            }
            postOverride = new VrPostOverride();
            particles = new ParticleAlignment();
            frame = new DioramaFrame();
            voxels = new VoxelTerrain();
            slabs = new SpriteSlabs();
            extruder = new ObjectExtruder();
            liquids = new LiquidVolume();
            ui = new VrUi { Frame = frame };
            if (Array.IndexOf(Args, "--cvr-dbg-noimgui") < 0) ImguiCapture.Start();   // flag: cost A/B
            greenscreen = new Greenscreen();
            rain = new VrRain();
            worldText = new WorldText();
            ears = new Ears();
            if (Array.IndexOf(Args, "--cvr-dbg-nofp") < 0) firstPerson = new FirstPerson();
            XrNative.cvr_xr_set_eye_textures(targets[0].GetNativeTexturePtr(), targets[1].GetNativeTexturePtr());
            submitEvent = XrNative.cvr_xr_get_submit_event();
            copiedFrom = null;
            if (Camera.main != null) SyncEyesFrom(Camera.main);

            anchored = false;
            manualRecenter = false;
            focusedFrames = 0;
            lastFrameIndex = 0;

            // The runtime paces the frame loop (xrWaitFrame) once the session runs (UpdatePacing).
            savedVSync = QualitySettings.vSyncCount;
            savedTargetFps = Application.targetFrameRate;
            pacedByRuntime = false;
            failedFeatures.Clear();
            runtimeRecenter = false;
            VrSpace.SetHeading(Quaternion.identity);
            PopupDepth.Scale = Plugin.PopupScale.Value;
            VrInput.ResetFocus();
            VrInput.Enabled = true;
            Active = true;
            Plugin.Log.LogInfo("VR started");
        }

        public void StopVr(string reason)
        {
            if (!Active) return;
            Active = false;
            VrInput.Enabled = false;   // releases every synthetic key
            HudScaler.Apply(false);
            VrHands.UpdateMinigamePivot(false);
            waitedThisFrame = false;
            // XR-004: tear down everything StartVr created, so the process returns to its pre-start state.
            for (int i = 0; i < 2; i++)
            {
                if (eyes[i])
                {
                    PopupDepth.Unregister(eyes[i]);
                    ScreenPassFilter.Forget(eyes[i]);
                    Destroy(eyes[i].gameObject);
                }
                eyes[i] = null;
            }
            frame?.Destroy();
            greenscreen?.Destroy();
            greenscreen = null;
            firstPerson?.Destroy();
            firstPerson = null;
            voxels?.Destroy();
            voxels = null;
            slabs?.Destroy();
            extruder?.Destroy();
            extruder = null;
            slabs = null;
            liquids?.Destroy();
            rain?.Destroy();
            rain = null;
            worldText?.Destroy();
            worldText = null;
            ears?.Destroy();
            ears = null;
            liquids = null;
            postOverride?.Destroy();
            particles?.Restore();
            particles = null;
            postOverride = null;
            ImguiCapture.Stop();
            ui?.Destroy();
            ui = null;
            frame = null;
            if (copiedFrom) copiedFrom.cullingMask |= (1 << DioramaFrame.Layer) | (1 << VrUi.OverlayLayer) | (1 << VoxelTerrain.Layer);
            copiedFrom = null;

            XrNative.cvr_xr_set_eye_textures(IntPtr.Zero, IntPtr.Zero);
            // Run the native teardown under a watchdog: a runtime that blocks in session teardown must
            // never hang the game (WER AppHangB1 at quit with VirtualDesktopXR, 2026-10-03).
            var shutdown = new Thread(XrNative.cvr_xr_shutdown) { IsBackground = true, Name = "CasualtyVR.XrShutdown" };
            shutdown.Start();
            if (!shutdown.Join(ShutdownTimeoutMs))
            {
                bridgeWedged = true;
                Plugin.Log.LogError($"OpenXR shutdown did not finish within {ShutdownTimeoutMs} ms; continuing without it (VR cannot restart this session)");
            }
            PumpNativeLog();
            for (int i = 0; i < 2; i++)
            {
                if (targets[i]) { targets[i].Release(); Destroy(targets[i]); }
                targets[i] = null;
            }
            if (pacedByRuntime) RestorePacing("VR stopped");
            Plugin.Log.LogInfo($"VR stopped ({reason}): {info.framesSubmitted} frames submitted, {info.framesDropped} dropped, {info.waitsSkipped} waits skipped");
        }

        private void UpdateSeatedOrigin(XrNative.FrameInfo f)
        {
            Vector3 head = (ToUnity(f.left.position) + ToUnity(f.right.position)) * 0.5f;
            bool positionTracked = f.positionValid != 0;
            // Only tracked frames count towards settling: a held position (dropout) is not a place to anchor.
            focusedFrames = info.sessionState == StateFocused && positionTracked ? focusedFrames + 1 : 0;
            if (runtimeRecenter && positionTracked)
            {
                // The runtime recentred LOCAL onto the head, heading included: anchor there, along its forward.
                seatedOrigin = head;
                VrSpace.SetHeading(Quaternion.identity);
                anchored = true;
                runtimeRecenter = manualRecenter = false;
                Plugin.Log.LogInfo($"Recentered (runtime): seated origin {head} m (LOCAL, Unity axes), heading 0");
            }
            else if (manualRecenter || (!anchored && focusedFrames >= SettleFrames))
            {
                seatedOrigin = head;
                // F9 / the menu's Recenter: in front of where you face now (yaw only). The automatic anchor keeps the
                // runtime's own forward.
                var heading = manualRecenter
                    ? VrSpace.YawOf(Quaternion.Slerp(VrSpace.ToUnity(f.left.orientation), VrSpace.ToUnity(f.right.orientation), 0.5f))
                    : Quaternion.identity;
                VrSpace.SetHeading(heading);
                anchored = true;
                Plugin.Log.LogInfo($"Recentered ({(manualRecenter ? "key" : "auto after settle")}): seated origin {head} m (LOCAL, Unity axes), heading {heading.eulerAngles.y:F1} deg");
                manualRecenter = false;
            }
            else if (!anchored)
            {
                seatedOrigin = head;   // until tracking settles, keep the diorama in front of the head
            }
        }

        public void Recenter() => manualRecenter = true;

        private static void Zoom(float factor)
        {
            // Fewer world units per metre = bigger blocks = zoomed in.
            Plugin.UnitsPerMetre.Value = Mathf.Clamp(Plugin.UnitsPerMetre.Value * factor, 8f, 80f);
            Plugin.Log.LogInfo($"Zoom: {Plugin.UnitsPerMetre.Value:F1} units/m (view height {45f / Plugin.UnitsPerMetre.Value:F2} m)");
        }

        private void SyncEyesFrom(Camera main)
        {
            if (copiedFrom) copiedFrom.cullingMask |= (1 << DioramaFrame.Layer) | (1 << VrUi.OverlayLayer) | (1 << VoxelTerrain.Layer);
            copiedFrom = main;
            // The frame is VR-only: hide its layer from the game's own camera.
            main.cullingMask &= ~(1 << DioramaFrame.Layer) & ~(1 << VrUi.OverlayLayer) & ~(1 << VoxelTerrain.Layer);
            var mainData = main.GetUniversalAdditionalCameraData();
            for (int i = 0; i < 2; i++)
            {
                var cam = eyes[i];
                bool wasEnabled = cam.enabled;
                cam.CopyFrom(main);
                // CopyFrom carries the source's matrices; they would ignore our pose (PHASE1_SPIKE_RECEIPT).
                cam.ResetWorldToCameraMatrix();
                cam.ResetProjectionMatrix();
                cam.orthographic = false;
                // Eyes see the VR-only frame; the UI overlay quads are drawn after post (VrUi.DrawInto) and the
                // desktop quad never reaches an eye.
                cam.cullingMask = (main.cullingMask | (1 << DioramaFrame.Layer) | (1 << VoxelTerrain.Layer)) & ~(1 << VrUi.OverlayLayer) & ~(1 << VrUi.DesktopLayer);
                eyeMask = cam.cullingMask;
                cam.targetTexture = targets[i];
                cam.depth = main.depth - 2 + i;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.enabled = wasEnabled;
                var data = cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = mainData.renderPostProcessing && !DbgNoPost;
                data.volumeLayerMask = VrPostOverride.AddTo(mainData.volumeLayerMask);
                data.antialiasing = AntialiasingMode.None;
                data.dithering = true;   // hides 8-bit banding in dark gradients now that film grain is off
            }
            Plugin.Log.LogInfo($"Eye cameras synced from '{main.name}'");
        }

        private static Camera LogicCamera() =>
            PlayerCamera.main != null ? PlayerCamera.main.GetComponent<Camera>() : Camera.main;

        private static Vector3 Focus()
        {
            var logic = LogicCamera();
            return logic != null ? new Vector3(logic.transform.position.x, logic.transform.position.y, 0f) : Vector3.zero;
        }

        private static Vector3 ToUnity(XrNative.Vec3 p) => VrSpace.ToUnity(p);

        private static void ApplyView(Camera cam, XrNative.View v)
        {
            // Diorama: the game plane (z=0) is centred DioramaDistance metres in front of the seated head origin.
            // First person: the seated origin is the character's eye (VrSpace).
            float s = VrSpace.Scale;
            cam.transform.SetPositionAndRotation(VrSpace.Point(VrSpace.ToUnity(v.position)), VrSpace.Rot(VrSpace.ToUnity(v.orientation)));

            // First person looks along the world's rows: with the diorama's far plane (4000) every sprite, light and
            // chunk across the whole world in that direction was culled, sorted and drawn (40 fps, CPU-bound, 2026-10-05).
            float n = 0.05f * s, f = VrSpace.FirstPerson ? Mathf.Max(10f, Plugin.FpViewDistance.Value) : 4000f;
            var proj = Matrix4x4.Frustum(n * Mathf.Tan(v.fov.left), n * Mathf.Tan(v.fov.right),
                                         n * Mathf.Tan(v.fov.down), n * Mathf.Tan(v.fov.up), n, f);
            // Unity stores D3D render textures bottom-up; the swapchain copy is a raw CopyResource, so the
            // eye image is flipped at projection time instead (verified in the xr-sim compositor capture).
            if (Plugin.FlipY.Value) proj = Matrix4x4.Scale(new Vector3(1f, -1f, 1f)) * proj;
            cam.nearClipPlane = n;
            cam.farClipPlane = f;
            cam.projectionMatrix = proj;
        }

        private void OnEndCameraRendering(UnityEngine.Rendering.ScriptableRenderContext context, Camera cam)
        {
            if (!Active || ui == null) return;
            for (int i = 0; i < 2; i++)
                if (cam == eyes[i]) ui.DrawInto(context, cam, targets[i]);
        }

        private bool IsEye(Camera cam) => cam == eyes[0] || cam == eyes[1];
        public bool IsEyeCamera(Camera cam) => cam != null &&
            (Active && IsEye(cam) || DesktopFirstPerson.IsRenderCamera(cam));

        private void LogPerf()
        {
            statFrames++;
            statWorst = Mathf.Max(statWorst, Time.unscaledDeltaTime);
            float ms = Time.unscaledDeltaTime * 1000f;
            frameMs.Add(ms);
            // A frame that missed its display slot (the runtime paces at periodMs): its number, to find periodic hitches.
            if (ms > periodMs * 1.5f && slowFrames.Count < 64) slowFrames.Add(Time.frameCount);
            if (Time.unscaledTime < nextLogPump) return;
            float span = Time.unscaledTime - (nextLogPump - 2f);
            frameMs.Sort();
            int over = 0;
            foreach (var f in frameMs) if (f > periodMs * 1.5f) over++;
            string dist = frameMs.Count == 0 ? "" : $"frame p50 {frameMs[frameMs.Count / 2]:F1} p99 {frameMs[Mathf.Min(frameMs.Count - 1, (int)(frameMs.Count * 0.99f))]:F1} ms, over 1.5x period {over}/{frameMs.Count} (period {periodMs:F1} ms), ";
            if (Plugin.Verbose && slowFrames.Count > 0) Plugin.Log.LogInfo($"perf: slow frames {string.Join(",", slowFrames)}");
            frameMs.Clear();
            slowFrames.Clear();
            if (Plugin.Verbose) Plugin.Log.LogInfo($"perf: {statFrames / Mathf.Max(span, 1e-3f):F1} fps, worst {statWorst * 1000f:F1} ms, {dist}popup {PopupDepth.LastApplyMs:F2} ms/{PopupDepth.LastCount} (skipped {PopupDepth.LastSkipped}), voxels {voxels?.ChunkCount ?? 0} chunks/{voxels?.PendingCount ?? 0} pending/{voxels?.LastBuildMs ?? 0f:F1} ms, slabs {slabs?.SourceCount ?? 0}, extruded {extruder?.Count ?? 0}, liquid surface {liquids?.SurfaceCells ?? 0}, submitted {info.framesSubmitted}, waits skipped {info.waitsSkipped}, state {info.sessionState}, head {VrSpace.HeadPosition} fwd {VrSpace.HeadForward}");
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (Plugin.Verbose && VrSpace.FirstPerson && eyes[0] != null) Plugin.Log.LogInfo($"fp: eye {eyes[0].transform.position} fwd {eyes[0].transform.forward} rot {VrSpace.Rotation.eulerAngles} scale {VrSpace.Scale:F2} facingRight {FirstPerson.FacingRightNow} billboards {FirstPerson.BillboardGroups} standing {(body != null && body.standing)} fpUpdate {FirstPerson.UpdateTime.Elapsed.TotalMilliseconds / Mathf.Max(1, statFrames):F2} ms/frame fpRender {FirstPerson.RenderTime.Elapsed.TotalMilliseconds / Mathf.Max(1, statFrames):F2} ms/frame popup {PopupDepth.LastApplyMs:F2} ms texts {(worldText != null ? worldText.Shown : 0)} rain {(rain != null ? $"{rain.Particles} playing {rain.Playing} alpha {(WorldGeneration.world != null && WorldGeneration.world.rainSprite != null ? WorldGeneration.world.rainSprite.color.a : -1f):F2}" : "-")}");
            if (Plugin.Verbose && VrSpace.FirstPerson) { string path = FirstPerson.PathStats(); if (path != null) Plugin.Log.LogInfo("fp path: " + path); }
            if (Plugin.Verbose) { string scans = Scans.Report(); if (scans != null) Plugin.Log.LogInfo("perf: scans (slowest ms) " + scans); }
            if (Plugin.Verbose && VrSpace.FirstPerson) Plugin.Log.LogInfo("fp " + VrGrab.Report());
            FirstPerson.UpdateTime.Reset();
            FirstPerson.RenderTime.Reset();
            if (Plugin.Verbose) Plugin.Log.LogInfo($"input: laser={(VrInput.LaserMode ? "on" : "off")} hit={VrInput.LaserHit} modWindow={VrInput.OnModWindow} mouse={VrInput.VirtualMouse} held=[{VrInput.HeldList()}] body={(body != null ? body.transform.position.ToString() : "-")} profile='{VrInput.Profile}'");
            nextLogPump = Time.unscaledTime + 2f;
            statFrames = 0;
            statWorst = 0f;
            PumpNativeLog();
        }

        private static void PumpNativeLog()
        {
            string log = XrNative.DrainLog();
            if (string.IsNullOrEmpty(log)) return;
            foreach (var line in log.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Players' logs keep the bridge's start/stop, warnings and errors, not the per-second timing.
                bool routine = line.Contains("timing/frame") || line.Contains("input: hand");
                if (Plugin.Verbose || !routine) Plugin.Log.LogInfo("[bridge] " + line);
            }
        }
    }
}
