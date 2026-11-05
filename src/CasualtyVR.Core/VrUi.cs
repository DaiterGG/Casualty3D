using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CasualtyVR
{
    /// <summary>
    /// Gets the game's screen-space UI into the headset (Phase 3, risk R9: overlay canvases only ever
    /// reach the window).
    ///
    /// Every root ScreenSpaceOverlay canvas is switched to ScreenSpaceCamera on a UI-only camera whose
    /// world coordinates equal screen pixels: it sits at (W/2, H/2, -PlaneDistance), orthographic size H/2,
    /// and the canvases sit on its z=0 plane. In that setup a canvas has the same position and scale it
    /// had as an overlay, so game code that writes pixel positions into UI transforms
    /// (`dragImage.rectTransform.position = Input.mousePosition`, the radial menu, tooltips) and
    /// `PlayerCamera.uiScale` keep working unchanged, and the EventSystem's raycasts map 1:1 because the
    /// UI texture has the window's size.
    ///
    /// The UI texture is shown three ways:
    ///   desktop  - a raw-image overlay canvas, so the monitor looks as before;
    ///   HUD      - mapped onto the diorama's window opening (= the flat camera's view), so the HUD sits on
    ///              the diorama's corners and screen-aligned UI (radial menu, drag icon) lines up with the world;
    ///   dashboard- a panel below the diorama, tilted towards the player, when a menu/minigame is open.
    /// All of it is reverted when VR stops (XR-004).
    /// </summary>
    internal sealed class VrUi
    {
        public const int UiLayer = 30;
        private const float PlaneDistance = 100f;

        private readonly Camera uiCam;
        public Camera UiCamera => uiCam;
        private RenderTexture rt;
        public const int DesktopLayer = 29;   // seen only by the game's own camera (desktop mirror)
        // HUD / dashboard / main-menu quads: no camera renders this layer. They are drawn straight into each
        // finished eye image by DrawInto (after post-processing, so no bloom; user report "too glowy"), which
        // costs a few draw calls instead of an overlay camera's full 2D-renderer pass (90 -> 80 fps measured).
        public const int OverlayLayer = 31;
        private readonly UnityEngine.Rendering.CommandBuffer overlayCmd = new UnityEngine.Rendering.CommandBuffer { name = "CasualtyVR.UiOverlay" };
        private readonly GameObject desktop;
        private readonly GameObject hud;
        private readonly GameObject dashboard;
        private readonly GameObject theatre;   // main menu: the whole UI, big and centred where the diorama sits
        private readonly Material panelMaterial;      // opaque dashboard
        private readonly Material additiveMaterial;   // HUD over the diorama, and the desktop mirror
        private readonly Material imguiMaterial;      // captured IMGUI windows (ImguiCapture), over every panel
        // The VR settings panel (Phase 3): a dark see-through backing, then the UI texture added on top, as the game's
        // own screen is composited (its UI is white lines on black: the black lets the scene through, the lines glow).
        private readonly Material backingMaterial = new Material(Shader.Find("Sprites/Default")) { name = "CasualtyVR.SettingsBacking", color = new Color(0.010f, 0.014f, 0.040f, 0.86f) };
        private readonly GameObject desktopImgui;     // ...and over the desktop mirror
        private readonly Dictionary<Canvas, int> converted = new Dictionary<Canvas, int>();   // canvas -> original layer
        private float nextScan;
        private bool selfTestDone;
        private float settledSince = -1f;
        private bool lastMenuScene;

        public bool DashboardShown { get; private set; }
        private readonly Mesh cursorMesh = CreateCursorMesh();
        private readonly Mesh beamMesh = CreateBeamMesh();
        private readonly Mesh reticleMesh = CreateReticleMesh();
        private readonly Material beamMaterial = new Material(Shader.Find("Sprites/Default")) { name = "CasualtyVR.Beam" };
        private readonly Material cursorMaterial = new Material(Shader.Find("Sprites/Default")) { name = "CasualtyVR.Cursor" };
        private float unitsPerMetreForCursor = 25f;

        /// <summary>F11 (or --cvr-dbg-dashboard): show the dashboard even when no menu is open.</summary>
        public bool ForceDashboard = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--cvr-dbg-dashboard") >= 0;

        public DioramaFrame Frame;

        /// <summary>The running instance (VR on), for the settings menu's headless aim check.</summary>
        public static VrUi Current { get; private set; }

        // The VR settings menu on its own panel (VRMENU_REVIEW M1a): the dashboard shows only the menu's area of the UI
        // texture (dashUv, normalised), filling a bigger panel, placed in front of your head when it opens (M5b).
        private Rect dashUv = new Rect(0f, 0f, 1f, 1f);
        private bool settingsPlaced;
        private Vector3 settingsPos;        // tracking space
        private Quaternion settingsRot = Quaternion.identity;
        /// <summary>The settings menu is shown on its own panel this frame.</summary>
        public bool SettingsPanelShown { get; private set; }
        /// <summary>The game menu zoomed on the dashboard (V7: crafting, wound view, trade, inventory in first person), or null.</summary>
        public string ZoomedMenu { get; private set; }

        /// <summary>Place the settings panel in front of the head again on the next frame (menu opened, recentred).</summary>
        public void PlaceSettingsAgain() => settingsPlaced = false;

        public VrUi()
        {
            Current = this;
            var camGo = new GameObject("CasualtyVR.UiCamera") { hideFlags = HideFlags.DontSave };
            Object.DontDestroyOnLoad(camGo);
            uiCam = camGo.AddComponent<Camera>();
            uiCam.orthographic = true;
            uiCam.cullingMask = 1 << UiLayer;
            uiCam.clearFlags = CameraClearFlags.SolidColor;
            uiCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            uiCam.nearClipPlane = 1f;
            uiCam.farClipPlane = PlaneDistance * 2f;
            uiCam.depth = 50;
            // No HDR: URP would render through an R11G11B10 intermediate (no alpha channel) and the UI
            // texture would come out opaque black wherever there is no UI (seen in xr-sim capture).
            uiCam.allowHDR = false;
            uiCam.allowMSAA = false;
            // The game's full-screen effect passes must not paint over the UI (pink wash while dying).
            ScreenPassFilter.Exclude(uiCam);
            var data = uiCam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;

            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            panelMaterial = new Material(shader) { name = "CasualtyVR.UiPanel" };
            // URP's 2D renderer writes alpha = 1 into the UI texture whatever the clear colour (read back
            // in gameplay: empty areas are (0,0,0,1) while every full-screen UI image has alpha 0). The HUD
            // is glowing outlines and text, so it is composited additively: black adds nothing.
            additiveMaterial = CreateAdditiveMaterial();
            hud = CreateQuad("CasualtyVR.UiHud", short.MaxValue, additiveMaterial);
            dashboard = CreateQuad("CasualtyVR.UiDashboard", short.MaxValue, panelMaterial);
            theatre = CreateQuad("CasualtyVR.UiTheatre", short.MaxValue, panelMaterial);
            // Desktop: same additive composite, on a quad in front of the game camera that only it sees
            // (UGUI cannot draw the URP particle shader, and the opaque texture would black out the monitor).
            desktop = CreateQuad("CasualtyVR.UiDesktop", short.MaxValue - 1, additiveMaterial, DesktopLayer);
            // Mods' OnGUI windows (ImguiCapture): alpha-blended over the UI, as IMGUI draws over the canvases.
            imguiMaterial = CreateBlendMaterial(UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha, "CasualtyVR.UiImgui");
            desktopImgui = CreateQuad("CasualtyVR.UiDesktopImgui", short.MaxValue, imguiMaterial, DesktopLayer);

            EnsureTexture();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static Material CreateAdditiveMaterial() => CreateBlendMaterial(UnityEngine.Rendering.BlendMode.One, "CasualtyVR.UiAdditive");

        // Source factor One: additive with DstBlend One; with OneMinusSrcAlpha it is premultiplied-alpha blending, which
        // suits the IMGUI capture (IMGUI's own SrcAlpha/OneMinusSrcAlpha blend leaves colour times alpha in a cleared
        // texture; partly transparent window backgrounds come out a little more see-through than on the monitor).
        private static Material CreateBlendMaterial(UnityEngine.Rendering.BlendMode dst, string name)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
            {
                Plugin.Log.LogWarning("UI: URP Particles/Unlit not found; HUD will be opaque");
                return new Material(Shader.Find("Sprites/Default"));
            }
            var m = new Material(shader) { name = name, renderQueue = 3000 };
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", dst == UnityEngine.Rendering.BlendMode.One ? 2f : 1f);   // additive / premultiply
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_DstBlend", (float)dst);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetColor("_BaseColor", Color.white);
            return m;
        }

        private GameObject CreateQuad(string name, int order, Material material, int layer = OverlayLayer)
        {
            var go = new GameObject(name) { layer = layer, hideFlags = HideFlags.DontSave };
            Object.DontDestroyOnLoad(go);
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f), new Vector3(0.5f, 0.5f), new Vector3(-0.5f, 0.5f) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };   // both windings (projection Y flip)
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.sortingLayerName = "LineOfSight";
            r.sortingOrder = order;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.SetActive(false);
            return go;
        }

        private void EnsureTexture()
        {
            int w = Screen.width, h = Screen.height;
            if (rt != null && rt.width == w && rt.height == h) return;
            if (rt != null) { uiCam.targetTexture = null; rt.Release(); Object.Destroy(rt); }
            rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "CasualtyVR.UiTexture" };
            rt.Create();
            uiCam.targetTexture = rt;
            uiCam.orthographicSize = h * 0.5f;
            uiCam.transform.SetPositionAndRotation(new Vector3(w * 0.5f, h * 0.5f, -PlaneDistance), Quaternion.identity);
            panelMaterial.mainTexture = rt;
            additiveMaterial.mainTexture = rt;
            if (additiveMaterial.HasProperty("_BaseMap")) additiveMaterial.SetTexture("_BaseMap", rt);
            Plugin.Log.LogInfo($"UI texture {w}x{h}");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            nextScan = 0f;
            selfTestDone = false;
        }

        /// <summary>Per frame, before the eye cameras render.</summary>
        public void Update(Vector3 focus, Camera logic, Vector3 seatedOrigin, float unitsPerMetre)
        {
            unitsPerMetreForCursor = VrSpace.Scale;
            EnsureTexture();
            ImguiCapture.EnsureTexture();
            if (ImguiCapture.Texture != null && imguiMaterial.mainTexture != ImguiCapture.Texture)
            {
                imguiMaterial.mainTexture = ImguiCapture.Texture;
                if (imguiMaterial.HasProperty("_BaseMap")) imguiMaterial.SetTexture("_BaseMap", ImguiCapture.Texture);
            }
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 0.5f;
                ConvertCanvases();
            }

            // Main menu / pre-run scene (no PlayerCamera): the whole screen is UI, so show it where the
            // diorama is, opaque, covering the menu scene's starfield (user report: menu "too low", with a
            // second "space/stars" window above it).
            // Full-screen game states (main menu, death/end-of-run screen) are shown big at eye level. On the
            // low dashboard the end screen's receipt and its click-to-continue text sat at the bottom edge of
            // the headset's view (user report 2026-10-03: death screen not usable in VR).
            // ...and the world-generation loading screen, which otherwise went to the additive HUD and showed
            // the level through it (user report).
            bool menuScene = PlayerCamera.main == null || EndScreenShown() || WorldGenerating();
            // The settings menu is modal: on its own panel, also over the main menu (instead of the theatre).
            Rect menuRect = VrSettingsMenu.IsOpen ? VrSettingsMenu.Instance.ScreenRect() : Rect.zero;
            bool settingsOpen = menuRect.width > 1f && menuRect.height > 1f;
            // V7 (user, 2026-10-18): the game's own menus are zoomed the same way, at the settings panel's pixel size.
            string zoomed = null;
            if (!settingsOpen && !menuScene && Plugin.ZoomGameMenus.Value && GameMenuRect(out var gameRect, out zoomed)) menuRect = gameRect;
            bool settingsPanel = settingsOpen || zoomed != null;
            if (!settingsPanel || zoomed != ZoomedMenu) settingsPlaced = false;
            if (zoomed != ZoomedMenu) Plugin.Log.LogInfo($"UI: game menu zoom {(zoomed != null ? $"on: {zoomed} ({menuRect.width:F0}x{menuRect.height:F0} px)" : "off")}");
            if (zoomed != ZoomedMenu && zoomed != null && Plugin.Verbose) DumpMenuParts();
            ZoomedMenu = zoomed;
            SettingsPanelShown = settingsOpen;
            SetDashboardUv(settingsPanel
                ? Rect.MinMaxRect(Mathf.Clamp01(menuRect.xMin / rt.width), Mathf.Clamp01(menuRect.yMin / rt.height), Mathf.Clamp01(menuRect.xMax / rt.width), Mathf.Clamp01(menuRect.yMax / rt.height))
                : new Rect(0f, 0f, 1f, 1f));
            if (settingsPanel) menuScene = false;
            theatre.SetActive(menuScene);
            bool wasShown = DashboardShown;
            DashboardShown = !menuScene && (ForceDashboard || settingsPanel || MenuOpen());
            if (DashboardShown != wasShown || menuScene != lastMenuScene)
                Plugin.Log.LogInfo($"UI mode: {(menuScene ? "theatre (main menu / loading / end screen)" : DashboardShown ? "dashboard" : "HUD")}");
            lastMenuScene = menuScene;
            PlaceDesktopMirror();
            hud.SetActive(!menuScene && !DashboardShown && logic != null);
            dashboard.SetActive(DashboardShown);

            float frontZ = -3f * PopupDepth.Scale - 0.05f;   // just in front of the frame (PopupDepth front band)
            if (theatre.activeSelf)
            {
                // Sized in metres (not by zoom): at the diorama's full width the menu ran past both eyes' view.
                float w = Plugin.MenuWidth.Value * unitsPerMetre;
                theatre.transform.SetPositionAndRotation(new Vector3(focus.x, focus.y, frontZ), Quaternion.identity);
                theatre.transform.localScale = new Vector3(w, w * rt.height / rt.width, 1f);
            }
            if (hud.activeSelf && VrSpace.FirstPerson)
            {
                // First person: the HUD is a see-through panel in front of you (tracking space), facing you.
                float w = Plugin.MenuWidth.Value;
                hud.transform.SetPositionAndRotation(VrSpace.Point(VrSpace.Seated(seatedOrigin, new Vector3(0f, -0.05f, Plugin.DioramaDistance.Value))), VrSpace.Rot(VrSpace.SeatedRot(Quaternion.identity)));
                hud.transform.localScale = new Vector3(w, w * rt.height / rt.width, 1f) * VrSpace.Scale;
            }
            else if (hud.activeSelf)
            {
                float halfH = logic.orthographic ? logic.orthographicSize : 22.5f;
                // On the game plane (z=0): the laser then hits one plane for world objects and HUD elements, so
                // the cursor, beam end and click agree (playbook HUD-001). In front of it they parallaxed apart.
                hud.transform.SetPositionAndRotation(new Vector3(focus.x, focus.y, 0f), Quaternion.identity);
                hud.transform.localScale = new Vector3(halfH * 2f * logic.aspect, halfH * 2f, 1f);
            }
            if (dashboard.activeSelf && settingsPanel)
            {
                if (!settingsPlaced)
                {
                    // In front of where you look (yaw only), at a comfortable height, tilted to face the eyes (M5b).
                    settingsPlaced = true;
                    Quaternion yaw = VrSpace.YawOf(VrSpace.HeadTrackingRot);
                    var local = new Vector3(0f, Plugin.SettingsHeight.Value, Plugin.SettingsDistance.Value);
                    settingsPos = VrSpace.HeadTrackingPos + yaw * local;
                    settingsRot = yaw * Quaternion.LookRotation(local.normalized, Vector3.up);
                }
                dashboard.transform.SetPositionAndRotation(VrSpace.Point(settingsPos), VrSpace.Rot(settingsRot));
                // A game menu: as many metres per pixel as the settings panel, so the two read the same.
                float wMetres = zoomed == null ? Plugin.SettingsWidth.Value
                    : Mathf.Min(1.0f, Plugin.SettingsWidth.Value * menuRect.width / Mathf.Max(1f, VrSettingsMenu.PanelPixelWidth()));   // the wound view spans the screen: at 1.2 m its buttons left the view
                float aspect = (dashUv.height * rt.height) / Mathf.Max(1f, dashUv.width * rt.width);
                // ...and no taller than 40 degrees at its distance, so it fits the view: at the settings' pixel size the
                // crafting panel was 0.72 m tall and its top and bottom left the headset's view (user, 2026-10-23).
                if (zoomed != null)
                {
                    float maxH = 2f * Plugin.SettingsDistance.Value * Mathf.Tan(20f * Mathf.Deg2Rad);
                    if (wMetres * aspect > maxH) wMetres = maxH / aspect;
                }
                dashboard.transform.localScale = new Vector3(wMetres, wMetres * aspect, 1f) * VrSpace.Scale;
            }
            else if (dashboard.activeSelf)
            {
                // Seated-space pose: below and in front of the head, tilted to face it. First person, minigame: close
                // and low, within reach, so the hand touches it (FP-4).
                bool touchPanel = VrSpace.FirstPerson && MinigameBase.main != null && MinigameBase.main.currentMinigame && Plugin.FpMinigameTouch.Value;
                Vector3 local = touchPanel ? new Vector3(0f, -0.32f, 0.42f) : new Vector3(0f, Plugin.DashboardHeight.Value, Plugin.DashboardDistance.Value);
                Vector3 toward = (local - Vector3.zero).normalized;
                Quaternion rot = Quaternion.LookRotation(toward, Vector3.up);
                dashboard.transform.SetPositionAndRotation(VrSpace.Point(VrSpace.Seated(seatedOrigin, local)), VrSpace.Rot(VrSpace.SeatedRot(rot)));
                float wMetres = touchPanel ? 0.62f : Plugin.DashboardWidth.Value;
                dashboard.transform.localScale = new Vector3(wMetres, wMetres * rt.height / rt.width, 1f) * VrSpace.Scale;
            }

            // Diagnostics once per scene, after the loading screen: in gameplay only once the world is generated.
            bool settled = WorldGeneration.world == null
                ? Time.timeSinceLevelLoad > 3f
                : !WorldGeneration.world.generatingWorld && PlayerCamera.main != null && PlayerCamera.main.body != null;
            if (!settled) settledSince = -1f;
            else if (settledSince < 0f) settledSince = Time.unscaledTime;
            if (!selfTestDone && converted.Count > 0 && settledSince >= 0f && Time.unscaledTime - settledSince > 3f) { if (Plugin.Verbose) { SelfTest(); ProbeAlpha(); } else selfTestDone = true; }
        }

        private void PlaceDesktopMirror()
        {
            var cam = Camera.main;
            desktop.SetActive(cam != null && cam.orthographic);
            if (!desktop.activeSelf) { if (desktopImgui.activeSelf) desktopImgui.SetActive(false); return; }
            float h = cam.orthographicSize * 2f;
            desktop.transform.SetPositionAndRotation(cam.transform.position + cam.transform.forward * (cam.nearClipPlane + 0.05f), cam.transform.rotation);
            desktop.transform.localScale = new Vector3(h * cam.aspect, h, 1f);
            bool imgui = ImguiCapture.Running && ImguiCapture.HasContent && ImguiCapture.Texture != null;
            if (desktopImgui.activeSelf != imgui) desktopImgui.SetActive(imgui);
            if (!imgui) return;
            desktopImgui.transform.SetPositionAndRotation(cam.transform.position + cam.transform.forward * (cam.nearClipPlane + 0.04f), cam.transform.rotation);
            desktopImgui.transform.localScale = desktop.transform.localScale;
        }

        private void SetDashboardUv(Rect uv)
        {
            if (uv == dashUv) return;
            dashUv = uv;
            var mesh = dashboard.GetComponent<MeshFilter>().sharedMesh;
            mesh.uv = new[] { new Vector2(uv.xMin, uv.yMin), new Vector2(uv.xMax, uv.yMin), new Vector2(uv.xMax, uv.yMax), new Vector2(uv.xMin, uv.yMax) };
        }

        /// <summary>A UI-texture pixel to the panel's local coordinates (-0.5..0.5), through the dashboard's crop.</summary>
        private Vector2 PanelLocal(GameObject panel, Vector2 pixel)
        {
            Vector2 n = new Vector2(pixel.x / rt.width, pixel.y / rt.height);
            if (panel == dashboard) n = new Vector2((n.x - dashUv.x) / dashUv.width, (n.y - dashUv.y) / dashUv.height);
            return n - new Vector2(0.5f, 0.5f);
        }

        /// <summary>Panel coordinates (0..1) to a UI-texture pixel, through the dashboard's crop.</summary>
        private Vector2 PanelPixel(GameObject panel, float u, float v)
        {
            if (panel == dashboard) { u = dashUv.x + u * dashUv.width; v = dashUv.y + v * dashUv.height; }
            return new Vector2(u * rt.width, v * rt.height);
        }

        /// <summary>Where a UI-texture pixel is in the world on the settings panel (headless aim check), or false.</summary>
        public bool SettingsPixelToWorld(Vector2 pixel, out Vector3 world)
        {
            world = Vector3.zero;
            if (!SettingsPanelShown || !dashboard.activeSelf) return false;
            Vector2 l = PanelLocal(dashboard, pixel);
            world = dashboard.transform.TransformPoint(new Vector3(l.x, l.y, 0f));
            return true;
        }

        /// <summary>
        /// V7: the open game menu's rectangle in UI-texture pixels, at its final size (the crafting panel opens from 10 %
        /// and the inventory from 1 %; the crop must not grow with the animation), with a small margin. Not while the
        /// pause menu, a minigame or the console is up (they would be cropped away), and the inventory only in first
        /// person: in the diorama it is on the HUD, lined up with the world for dragging items out, and already big.
        /// </summary>
        private bool GameMenuRect(out Rect px, out string name)
        {
            px = default;
            name = null;
            var pc = PlayerCamera.main;
            if (pc == null || PauseHandler.paused) return false;
            if (MinigameBase.main != null && MinigameBase.main.currentMinigame) return false;
            if (ConsoleScript.instance != null && ConsoleScript.instance.active) return false;
            bool any = false;
            Rect r = default;
            void Add(RectTransform t, RectTransform root)
            {
                if (t == null || root == null) return;
                var f = FinalRect(t, root);
                r = any ? Rect.MinMaxRect(Mathf.Min(r.xMin, f.xMin), Mathf.Min(r.yMin, f.yMin), Mathf.Max(r.xMax, f.xMax), Mathf.Max(r.yMax, f.yMax)) : f;
                any = true;
            }
            // The wound view's and the inventory's own rectangles are the whole screen: frame their visible parts, without
            // the full-screen fades, the inventory's 5000-px drag edges and its 1280-px clothing strip (measured, verbose
            // "UI zoom part" lines).
            void AddParts(RectTransform root)
            {
                if (root == null) return;
                foreach (Transform c in root)
                {
                    if (!c.gameObject.activeSelf || !(c is RectTransform crt) || c.GetComponent<Graphic>() == null) continue;
                    var f = FinalRect(crt, root);
                    if (f.width > rt.width * 0.6f || f.height > rt.height * 0.9f) continue;
                    Add(crt, root);
                }
            }
            if (Active(pc.woundView))
            {
                // The health screen spans the whole width, and the HUD buttons along the bottom (the heart that toggles it,
                // the icons at the bottom right) are used with it: framed by its parts they were cut in half (user,
                // 2026-10-23). It gets the whole screen, at most 1 m wide.
                name = "wound view";
                px = new Rect(0f, 0f, rt.width, rt.height);
                return true;
            }
            else if (Active(pc.craftingPanel))
            {
                name = "crafting";
                Add(pc.craftingPanel.transform as RectTransform, pc.craftingPanel.transform as RectTransform);
            }
            else if (Active(pc.tradeMenu))
            {
                name = "trade";
                Add(pc.tradeMenu.transform as RectTransform, pc.tradeMenu.transform as RectTransform);
                if (pc.radialMenu != null && pc.radialMenu.gameObject.activeInHierarchy) AddParts(pc.radialMenu);
            }
            else if (VrSpace.FirstPerson && pc.radialOpen && pc.radialMenu != null && pc.radialMenu.gameObject.activeInHierarchy)
            {
                name = "inventory";
                AddParts(pc.radialMenu);
            }
            if (!any) { name = null; return false; }
            const float margin = 16f;
            px = Rect.MinMaxRect(Mathf.Max(0f, r.xMin - margin), Mathf.Max(0f, r.yMin - margin), Mathf.Min(rt.width, r.xMax + margin), Mathf.Min(rt.height, r.yMax + margin));
            return px.width > 16f && px.height > 16f;
        }

        // Verbose: the open menu's direct children and their final rectangles (which parts the zoom should frame).
        private void DumpMenuParts()
        {
            var pc = PlayerCamera.main;
            foreach (var root in new[] { pc.woundView != null ? pc.woundView.transform as RectTransform : null, pc.radialMenu, pc.craftingPanel != null ? pc.craftingPanel.transform as RectTransform : null })
            {
                if (root == null || !root.gameObject.activeInHierarchy) continue;
                foreach (Transform c in root)
                    if (c is RectTransform crt) Plugin.Log.LogInfo($"UI zoom part: {root.name}/{c.name} active {c.gameObject.activeSelf} graphic {c.GetComponent<Graphic>() != null} rect {FinalRect(crt, root)}");
            }
        }

        /// <summary>
        /// A RectTransform's rectangle in UI-texture pixels as it will be once root has finished its opening animation (root's
        /// own scale taken as 1). The UI camera maps world units to pixels, so a canvas' world positions are pixels.
        /// </summary>
        private static Rect FinalRect(RectTransform t, RectTransform root)
        {
            var c = new Vector3[4];
            t.GetLocalCorners(c);
            Vector3 a = root.InverseTransformPoint(t.TransformPoint(c[0])), b = root.InverseTransformPoint(t.TransformPoint(c[2]));
            Vector3 s = root.parent != null ? root.parent.lossyScale : Vector3.one;
            Vector2 o = root.position;
            return Rect.MinMaxRect(o.x + Mathf.Min(a.x, b.x) * s.x, o.y + Mathf.Min(a.y, b.y) * s.y, o.x + Mathf.Max(a.x, b.x) * s.x, o.y + Mathf.Max(a.y, b.y) * s.y);
        }

        internal static bool GameMenuOpen()
        {
            var pc = PlayerCamera.main;
            if (pc == null) return false;   // main menu is handled by theatre mode
            if (PauseHandler.paused) return true;
            // First person: the inventory radial (opened at the chest) is used on the panel with the laser.
            if (pc.radialOpen || (pc.radialMenu != null && pc.radialMenu.gameObject.activeInHierarchy)) return true;
            if (MinigameBase.main != null && MinigameBase.main.currentMinigame) return true;
            if (ConsoleScript.instance != null && ConsoleScript.instance.active) return true;
            if (Active(pc.woundView) || Active(pc.craftingPanel) || Active(pc.tradeMenu)) return true;
            return false;
        }

        private static bool MenuOpen() => GameMenuOpen() || VrSettingsMenu.IsOpen;

        private static bool Active(GameObject go) => go != null && go.activeInHierarchy;

        private static bool WorldGenerating() => WorldGeneration.world != null && WorldGeneration.world.generatingWorld;

        /// <summary>True while the whole UI is shown as an opaque panel (main menu, loading, end screen).</summary>
        public bool TheatreShown => theatre != null && theatre.activeSelf;

        private static bool EndScreenShown()
        {
            var pc = PlayerCamera.main;
            return pc != null && pc.endScreen != null && pc.endScreen.gameObject.activeInHierarchy;
        }

        private void ConvertCanvases()
        {
            foreach (var c in Object.FindObjectsOfType<Canvas>(true))
            {
                if (!c.isRootCanvas || c.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                if (converted.ContainsKey(c)) continue;
                converted[c] = c.gameObject.layer;
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = uiCam;
                c.planeDistance = PlaneDistance;
                c.gameObject.layer = UiLayer;
                Plugin.Log.LogInfo($"UI: '{c.name}' (order {c.sortingOrder}) -> UI camera");
            }
            // Forget destroyed canvases.
            foreach (var dead in converted.Keys.Where(k => k == null).ToList()) converted.Remove(dead);
        }

        /// <summary>
        /// Headless proof that input still maps: raycast the EventSystem at the screen position of an
        /// active button in each converted canvas and require the button (or a child) to be hit.
        /// </summary>
        private void SelfTest()
        {
            selfTestDone = true;
            var es = EventSystem.current;
            if (es == null) { Plugin.Log.LogInfo("UI self-test: no EventSystem in this scene"); return; }
            int tested = 0, hit = 0;
            foreach (var c in converted.Keys)
            {
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                var button = c.GetComponentsInChildren<Selectable>().FirstOrDefault(s => s.isActiveAndEnabled && s.interactable && s.targetGraphic != null && s.targetGraphic.raycastTarget);
                if (button == null) continue;
                tested++;
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(uiCam, button.transform.position);
                var results = new List<RaycastResult>();
                es.RaycastAll(new PointerEventData(es) { position = screen }, results);
                bool ok = results.Count > 0 && (results[0].gameObject == button.gameObject || results[0].gameObject.transform.IsChildOf(button.transform));
                if (ok) hit++;
                Plugin.Log.LogInfo($"UI self-test: '{c.name}/{button.name}' at {screen} -> {(results.Count > 0 ? results[0].gameObject.name : "nothing")} {(ok ? "OK" : "MISS")}");
            }
            Plugin.Log.LogInfo($"UI self-test: {hit}/{tested} buttons hit through the UI camera");
        }

        /// <summary>Diagnostic: RGBA of the UI texture at the centre and a corner region with no UI.</summary>
        private void ProbeAlpha()
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            foreach (var (name, x, y) in new[] { ("centre", rt.width / 2, rt.height / 2), ("mid-left", rt.width / 4, rt.height / 2) })
            {
                tex.ReadPixels(new Rect(x, y, 1, 1), 0, 0);
                tex.Apply();
                Plugin.Log.LogInfo($"UI alpha probe {name}: {tex.GetPixel(0, 0)} (format {rt.graphicsFormat}, cam HDR {uiCam.allowHDR}, target {uiCam.targetTexture?.name})");
            }
            var full = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            full.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            full.Apply();
            string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "CasualtyVR", "ui-texture.png");
            System.IO.File.WriteAllBytes(path, full.EncodeToPNG());
            Plugin.Log.LogInfo("UI texture dumped: " + path);
            Object.Destroy(full);
            RenderTexture.active = prev;
            Object.Destroy(tex);
            foreach (var c in converted.Keys)
                if (c != null && c.gameObject.activeInHierarchy)
                    foreach (var g in c.GetComponentsInChildren<Graphic>())
                        if (g.isActiveAndEnabled && g.rectTransform.rect.width * g.transform.lossyScale.x >= rt.width * 0.9f)
                            Plugin.Log.LogInfo($"UI full-screen graphic: {c.name}/{g.name} {g.GetType().Name} color {g.color} tex {(g.mainTexture ? g.mainTexture.name : "-")} raycast {g.raycastTarget}");
        }

        /// <summary>Draws the active overlay quads into an eye's finished image (call from endCameraRendering).</summary>
        public void DrawInto(UnityEngine.Rendering.ScriptableRenderContext context, Camera eye, RenderTexture target)
        {
            overlayCmd.Clear();
            overlayCmd.SetRenderTarget(target);
            overlayCmd.ClearRenderTarget(true, false, Color.clear);   // depth only: overlays sit on top of the scene
            // renderIntoTexture=false: URP already flips on its final copy into the eye texture (verified in capture: true drew the HUD upside down).
            overlayCmd.SetViewProjectionMatrices(eye.worldToCameraMatrix, GL.GetGPUProjectionMatrix(eye.projectionMatrix, false));
            int drawn = 0;
            GameObject cursorPanel = null;
            // Passthrough greenscreen: the frame after post, so the key colour is exact.
            if (Frame != null && Frame.DrawnAfterPost && !VrSpace.FirstPerson)
            {
                overlayCmd.DrawMesh(Frame.Mesh, Frame.Matrix, Frame.KeyMaterial, 0, 0);
                drawn++;
            }
            foreach (var go in new[] { hud, dashboard, theatre })
            {
                if (!go.activeSelf) continue;
                var r = go.GetComponent<MeshRenderer>();
                var mesh = go.GetComponent<MeshFilter>().sharedMesh;
                if (go == dashboard && (SettingsPanelShown || ZoomedMenu != null))
                {
                    Vector2 inset = SettingsPanelShown ? VrSettingsMenu.BackingInset : Vector2.zero;
                    var back = go.transform.localToWorldMatrix * Matrix4x4.Scale(new Vector3(1f - 2f * inset.x, 1f - 2f * inset.y, 1f));
                    overlayCmd.DrawMesh(mesh, back, backingMaterial, 0, 0);
                    overlayCmd.DrawMesh(mesh, go.transform.localToWorldMatrix, additiveMaterial, 0, 0);
                    drawn += 2;
                }
                else
                {
                    overlayCmd.DrawMesh(mesh, go.transform.localToWorldMatrix, r.sharedMaterial, 0, 0);
                    drawn++;
                }
                // Mods' OnGUI windows on top of the game's UI, on whichever panel shows it.
                if (ImguiCapture.Running && ImguiCapture.HasContent && ImguiCapture.Texture != null) { overlayCmd.DrawMesh(mesh, go.transform.localToWorldMatrix, imguiMaterial, 0, 0); drawn++; }
                cursorPanel = go;
            }
            // First person, outside menus: the faint glow on what a hand would take (Phase 4b), under the comfort overlays.
            if (VrSpace.FirstPerson && !DashboardShown && !TheatreShown) drawn += VrGrab.DrawGlow(overlayCmd);
            drawn += FirstPerson.DrawOverlay(overlayCmd, eye);
            drawn += VrHands.Draw(overlayCmd, eye);
            // The OS cursor never reaches the headset: draw one where the mouse is on the active panel.
            // A minigame draws the game's own hand at the mouse: no second cursor on top (user, 2026-10-04).
            bool minigameHand = MinigameBase.main != null && MinigameBase.main.currentMinigame;
            bool fpWorld = VrSpace.FirstPerson && !DashboardShown && !TheatreShown;
            if (fpWorld)
            {
                // First person: the cursor is where the aim ray stops in the world (VrInput.WorldAim), as a small
                // reticle of constant apparent size, drawn on top.
                if (VrInput.LaserVisible && VrInput.LaserMode && Cursor.visible && !minigameHand && Plugin.CursorStyle.Value != "Off")
                {
                    Vector3 eyePos = eye.transform.position, at = VrInput.LaserEnd;
                    Vector3 toEye = eyePos - at;
                    float dist = toEye.magnitude;
                    if (dist > 1e-3f)
                    {
                        float size = dist * 0.014f;
                        var m = Matrix4x4.TRS(at + toEye * 0.05f, Quaternion.LookRotation(-toEye, eye.transform.up), new Vector3(size, size, size));
                        overlayCmd.DrawMesh(reticleMesh, m, cursorMaterial, 0, 0);
                        drawn++;
                    }
                }
            }
            else if (cursorPanel != null && Cursor.visible && !minigameHand)
            {
                // Cursor style (VR settings): the game's hand cut at the wrist, the arrow, or nothing.
                string style = Plugin.CursorStyle.Value;
                if (style == "Hand")
                {
                    if (!VrHands.DrawCursor(overlayCmd, CursorMatrix(cursorPanel, Plugin.HandCursorSize.Value)))
                        overlayCmd.DrawMesh(cursorMesh, CursorMatrix(cursorPanel), cursorMaterial, 0, 0);
                }
                else if (style != "Off") overlayCmd.DrawMesh(cursorMesh, CursorMatrix(cursorPanel), cursorMaterial, 0, 0);
            }
            if (VrInput.LaserVisible && Plugin.ShowLaser.Value)   // off by default: user found the beam distracting; the cursor marks the hit
            {
                Vector3 a = VrInput.LaserOrigin, b = VrInput.LaserEnd;
                float len = Vector3.Distance(a, b);
                if (len > 1e-3f)
                {
                    float w = 0.003f * unitsPerMetreForCursor;
                    var m = Matrix4x4.TRS(a, Quaternion.LookRotation(b - a), new Vector3(w, w, len));
                    beamMaterial.color = VrInput.LaserHit ? new Color(0.55f, 0.85f, 1f, 0.8f) : new Color(0.55f, 0.85f, 1f, 0.3f);
                    overlayCmd.DrawMesh(beamMesh, m, beamMaterial, 0, 0);
                    drawn++;
                }
            }
            if (drawn == 0) return;
            context.ExecuteCommandBuffer(overlayCmd);
            context.Submit();
        }

        /// <summary>
        /// The laser's single hit record: the visible theatre or dashboard panel if hit, else the game plane
        /// (the HUD quad spans exactly the flat camera's view). Returns the screen pixel the game should see.
        /// </summary>
        public bool RaycastActiveSurface(Ray ray, out Vector2 screen, out Vector3 point)
        {
            foreach (var panel in new[] { theatre, dashboard, hud })
            {
                if (panel == null || !panel.activeSelf) continue;
                if (panel == hud && VrSpace.FirstPerson) continue;   // first person aims into the world (VrInput.WorldAim)
                var t = panel.transform;
                var plane = new Plane(t.forward, t.position);
                if (!plane.Raycast(ray, out float dist) || dist <= 0f) continue;
                point = ray.GetPoint(dist);
                Vector3 local = t.InverseTransformPoint(point);
                float u = local.x + 0.5f, v = local.y + 0.5f;
                if (u < 0f || u > 1f || v < 0f || v > 1f) continue;
                screen = PanelPixel(panel, u, v);
                return true;
            }
            screen = Vector2.zero;
            point = Vector3.zero;
            return false;
        }

        /// <summary>First person: the laser on the HUD panel in front of you (for IMGUI windows, VrInput).</summary>
        public bool RaycastHud(Ray ray, out Vector2 screen, out Vector3 point)
        {
            screen = Vector2.zero;
            point = Vector3.zero;
            if (hud == null || !hud.activeSelf) return false;
            var t = hud.transform;
            var plane = new Plane(t.forward, t.position);
            if (!plane.Raycast(ray, out float dist) || dist <= 0f) return false;
            point = ray.GetPoint(dist);
            Vector3 local = t.InverseTransformPoint(point);
            float u = local.x + 0.5f, v = local.y + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
            screen = new Vector2(u * rt.width, v * rt.height);
            return true;
        }

        /// <summary>
        /// First person, minigames (FP-4): the hand itself is the pointer. If the point is within a few centimetres
        /// of the dashboard's surface, the mouse goes where it touches.
        /// </summary>
        public bool TouchDashboard(Vector3 point, out Vector2 screen, out Vector3 onPanel)
        {
            screen = Vector2.zero;
            onPanel = point;
            if (dashboard == null || !dashboard.activeSelf) return false;
            var t = dashboard.transform;
            Vector3 local = t.InverseTransformPoint(point);   // x, y in panel units; z in metres (the z scale is the world scale)
            float u = local.x + 0.5f, v = local.y + 0.5f;
            if (u < -0.05f || u > 1.05f || v < -0.05f || v > 1.05f || local.z < -0.10f || local.z > 0.15f) return false;
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);
            screen = PanelPixel(dashboard, u, v);
            onPanel = t.TransformPoint(new Vector3(u - 0.5f, v - 0.5f, 0f));
            return true;
        }

        private static Mesh CreateBeamMesh()
        {
            // Unit box from z=0 to z=1, x/y in [-0.5, 0.5]; scaled to the beam's width and length.
            var m = new Mesh { name = "CasualtyVR.Beam" };
            var v = new List<Vector3>();
            for (int i = 0; i < 8; i++) v.Add(new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? 0f : 1f));
            m.SetVertices(v);
            int[] faces = { 0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5 };
            var tris = new List<int>();
            for (int f = 0; f < faces.Length; f += 4)
            {
                int a = faces[f], b = faces[f + 1], c = faces[f + 2], d = faces[f + 3];
                tris.AddRange(new[] { a, b, c, a, c, d, a, c, b, a, d, c });
            }
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        private Matrix4x4 CursorMatrix(GameObject panel) => CursorMatrix(panel, Plugin.CursorSize.Value);

        private Matrix4x4 CursorMatrix(GameObject panel, float sizeMetres)
        {
            Vector2 l = PanelLocal(panel, Input.mousePosition);
            var local = new Vector3(Mathf.Clamp(l.x, -0.5f, 0.5f), Mathf.Clamp(l.y, -0.5f, 0.5f), 0f);
            var t = panel.transform;
            Vector3 pos = t.TransformPoint(local) - t.forward * (0.002f * unitsPerMetreForCursor);   // a hair in front of the panel
            float size = sizeMetres * unitsPerMetreForCursor;
            return Matrix4x4.TRS(pos, t.rotation, new Vector3(size, size, size));
        }

        private static Mesh CreateReticleMesh()
        {
            // Diamond: black outline, white centre (vertex colours), unit size, centred on the origin.
            var m = new Mesh { name = "CasualtyVR.Reticle" };
            m.vertices = new[] { new Vector3(0f, 0.5f), new Vector3(0.5f, 0f), new Vector3(0f, -0.5f), new Vector3(-0.5f, 0f),
                                 new Vector3(0f, 0.28f), new Vector3(0.28f, 0f), new Vector3(0f, -0.28f), new Vector3(-0.28f, 0f) };
            m.colors = new[] { Color.black, Color.black, Color.black, Color.black, Color.white, Color.white, Color.white, Color.white };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 4, 6, 5, 4, 7, 6 };
            m.RecalculateBounds();
            return m;
        }

        private static Mesh CreateCursorMesh()
        {
            // Arrow with its tip at the origin: black outline triangle, white inner triangle (vertex colours).
            var m = new Mesh { name = "CasualtyVR.Cursor" };
            m.vertices = new[] { new Vector3(-0.08f, 0.1f), new Vector3(-0.08f, -1.12f), new Vector3(0.84f, -0.7f),
                                 new Vector3(0f, 0f), new Vector3(0f, -0.9f), new Vector3(0.62f, -0.62f) };
            m.colors = new[] { Color.black, Color.black, Color.black, Color.white, Color.white, Color.white };
            m.triangles = new[] { 0, 1, 2, 0, 2, 1, 3, 4, 5, 3, 5, 4 };
            m.RecalculateBounds();
            return m;
        }

        public void Destroy()
        {
            if (Current == this) Current = null;
            overlayCmd.Release();
            Object.Destroy(cursorMesh);
            Object.Destroy(beamMesh);
            Object.Destroy(reticleMesh);
            Object.Destroy(beamMaterial);
            Object.Destroy(cursorMaterial);
            Object.Destroy(backingMaterial);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ScreenPassFilter.Forget(uiCam);
            foreach (var kv in converted)
            {
                if (kv.Key == null) continue;
                kv.Key.renderMode = RenderMode.ScreenSpaceOverlay;
                kv.Key.worldCamera = null;
                kv.Key.gameObject.layer = kv.Value;
            }
            converted.Clear();
            Object.Destroy(hud);
            Object.Destroy(dashboard);
            Object.Destroy(theatre);
            Object.Destroy(desktop);
            Object.Destroy(desktopImgui);
            Object.Destroy(imguiMaterial);
            Object.Destroy(uiCam.gameObject);
            if (rt != null) { rt.Release(); Object.Destroy(rt); }
            Object.Destroy(panelMaterial);
            Object.Destroy(additiveMaterial);
        }
    }
}
