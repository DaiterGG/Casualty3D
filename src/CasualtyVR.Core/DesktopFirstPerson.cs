using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace CasualtyVR
{
    [DefaultExecutionOrder(31000)]
    internal sealed class DesktopFirstPerson : MonoBehaviour
    {
        private const float PitchLimit = 80f;
        private const int MinimapWidth = 128;
        private const int MinimapHeight = 64;
        private static DesktopFirstPerson instance;
        private static Camera renderCamera;
        private FirstPerson firstPerson;
        private VoxelTerrain voxels;
        private ObjectExtruder extruder;
        private SpriteSlabs slabs;
        private LiquidVolume liquids;
        private WorldText worldText;
        private Camera configuredCamera;
        private bool cameraOrthographic;
        private float cameraFieldOfView, cameraNear, cameraFar, cameraOrthographicSize;
        private int cameraMask;
        private float yaw = 90f, pitch;
        private bool lookInitialized, cursorCaptured, desktopEnabled = true;
        private bool cursorStateSaved;
        private CursorLockMode savedLock;
        private bool savedCursorVisible;
        private bool keyForward, keyBack, keyJump, keyCrouch;
        private bool downForward, downBack, downJump, downCrouch, upForward, upBack, upJump, upCrouch;
        private bool projectedMovement;
        private float moveX, moveZ, movementSpeed, depthOffset;
        private static int aimDepthFrame = -1;
        private static Vector2 aimDepthScreen;
        private static Camera aimDepthCamera;
        private static float aimDepth;
        private static Canvas itemLabelCanvas;
        private static RectTransform itemLabelCanvasRect;
        private static CanvasScaler itemLabelCanvasScaler;
        private static readonly System.Collections.Generic.Dictionary<ItemLabel, ItemLabelLayout> ItemLabelLayouts =
            new System.Collections.Generic.Dictionary<ItemLabel, ItemLabelLayout>();
        private static readonly System.Collections.Generic.List<ItemLabel> ItemLabelScratch =
            new System.Collections.Generic.List<ItemLabel>();
        private static readonly int AimLayerMask = LayerMask.GetMask("Item", "Ground", "Descriptor", "Limb", "Body");
        private readonly System.Collections.Generic.List<(Light2D light, bool enabled)> disabledFlashlights =
            new System.Collections.Generic.List<(Light2D, bool)>();
        private readonly System.Collections.Generic.List<Graphic> radialGraphics = new System.Collections.Generic.List<Graphic>();
        private readonly Vector3[] radialCorners = new Vector3[4];
        private RectTransform centeredRadial;
        private Vector2 radialOriginalPosition, radialLastPosition;
        private RectTransform radialParent;
        private Canvas radialCanvas;
        private Texture2D minimapTexture;
        private Color32[] minimapPixels;
        private WorldGeneration minimapWorld;
        private Vector2Int minimapCenter;
        private float nextMinimapRefresh;
        private bool minimapNeedsRefresh = true;

        private struct ItemLabelLayout
        {
            public Transform Parent;
            public int SiblingIndex;
            public Vector2 AnchorMin, AnchorMax, Pivot, SizeDelta;
            public Vector3 LocalScale;
            public Quaternion LocalRotation;
        }

        public static bool IsRenderCamera(Camera cam) =>
            cam != null && cam == renderCamera && FirstPerson.Active;
        public static bool IsDesktopRenderActive =>
            renderCamera != null && FirstPerson.Active && (VrRig.Instance == null || !VrRig.Instance.Active);

        public static bool IsMovementActive => instance != null && instance.projectedMovement;
        public static float HorizontalMovementScale =>
            IsMovementActive && Mathf.Abs(instance.moveX) > 0.001f ? Mathf.Abs(instance.moveX) : 1f;

        public static void SetBaseMovementSpeed(float speed)
        {
            if (IsMovementActive && speed > 0f) instance.movementSpeed = speed;
        }

        public static void SuspendForVr() => instance?.DisposeDesktopFeatures();

        private static bool DesktopInputActive =>
            renderCamera != null && FirstPerson.Active && !VrUi.GameMenuOpen() && !VrSettingsMenu.IsOpen
            && (VrRig.Instance == null || !VrRig.Instance.Active);

        private void Awake()
        {
            instance = this;
            worldText = new WorldText();
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            Canvas.willRenderCanvases += CenterInventoryRadial;
        }

        private void Update()
        {
            if (VrRig.Instance != null && VrRig.Instance.Active) return;

            if (Input.GetKeyDown(Plugin.DesktopDioramaKey.Value)) desktopEnabled = false;
            if (Input.GetKeyDown(Plugin.FpKey.Value)) desktopEnabled = !desktopEnabled;
            if (FirstPerson.Active)
            {
                bool inventoryOpen = IsInventoryOpen();
                if (desktopEnabled && !VrUi.GameMenuOpen() && !VrSettingsMenu.IsOpen && !inventoryOpen)
                {
                    CaptureCursor();
                    float sensitivity = Mathf.Clamp(Plugin.DesktopMouseSensitivity.Value, 0.1f, 10f);
                    yaw += Input.GetAxisRaw("Mouse X") * sensitivity;
                    pitch = Mathf.Clamp(pitch - Input.GetAxisRaw("Mouse Y") * sensitivity, -PitchLimit, PitchLimit);
                }
                else
                {
                    ReleaseCursor();
                }
            }
            else ReleaseCursor();

            bool forward = PhysicalKey(KeyCode.W);
            bool back = PhysicalKey(KeyCode.S);
            bool left = PhysicalKey(KeyCode.A);
            bool right = PhysicalKey(KeyCode.D);
            bool jump = PhysicalKey(KeyCode.Space);
            bool crouch = PhysicalKey(KeyCode.LeftShift) || PhysicalKey(KeyCode.RightShift);

            projectedMovement = FirstPerson.Active && desktopEnabled && cursorCaptured
                && !VrUi.GameMenuOpen() && !VrSettingsMenu.IsOpen && !IsInventoryOpen();
            if (projectedMovement)
            {
                float forwardAxis = (forward ? 1f : 0f) - (back ? 1f : 0f);
                float strafeAxis = (right ? 1f : 0f) - (left ? 1f : 0f);
                float inputLength = Mathf.Sqrt(forwardAxis * forwardAxis + strafeAxis * strafeAxis);
                if (inputLength > 1f)
                {
                    forwardAxis /= inputLength;
                    strafeAxis /= inputLength;
                }

                float radians = yaw * Mathf.Deg2Rad;
                moveX = forwardAxis * Mathf.Sin(radians) + strafeAxis * Mathf.Cos(radians);
                moveZ = forwardAxis * Mathf.Cos(radians) - strafeAxis * Mathf.Sin(radians);
                float facingMove = moveX * (FirstPerson.FacingRightNow ? 1f : -1f);
                forward = facingMove > 0.001f;
                back = facingMove < -0.001f;
            }
            else
            {
                moveX = moveZ = 0f;
            }

            downForward = forward && !keyForward;
            downBack = back && !keyBack;
            downJump = jump && !keyJump;
            downCrouch = crouch && !keyCrouch;
            upForward = !forward && keyForward;
            upBack = !back && keyBack;
            upJump = !jump && keyJump;
            upCrouch = !crouch && keyCrouch;
            keyForward = forward;
            keyBack = back;
            keyJump = jump;
            keyCrouch = crouch;
        }

        private void LateUpdate()
        {
            if (VrRig.Instance != null && VrRig.Instance.Active)
            {
                LeaveDesktopView();
                return;
            }

            if (firstPerson == null)
            {
                firstPerson = new FirstPerson();
                voxels = new VoxelTerrain();
                extruder = new ObjectExtruder();
                slabs = new SpriteSlabs();
                liquids = new LiquidVolume();
            }

            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (!lookInitialized && body != null)
            {
                yaw = body.isRight ? 90f : -90f;
                lookInitialized = true;
            }

            PopupDepth.Scale = Plugin.PopupScale.Value;
            bool active = firstPerson.Update(false, Vector3.zero, forceFirstPerson: desktopEnabled, desktopYaw: yaw);
            if (!active)
            {
                projectedMovement = false;
                moveX = moveZ = 0f;
                depthOffset = 0f;
                LeaveDesktopView();
                return;
            }
            firstPerson.SetDesktopFlashlightMode(true);
            CenterInventoryRadial();

            var cam = PlayerCamera.main != null ? PlayerCamera.main.GetComponent<Camera>() : null;
            if (cam == null) return;
            ConfigureCamera(cam);

            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            var position = VrSpace.Anchor;
            float scale = Mathf.Max(FirstPerson.CurrentScale, 0.5f);
            float frontDepth = VoxelTerrain.FrontZ, backDepth = VoxelTerrain.BackZ;
            float depthMargin = Mathf.Min(0.35f, Mathf.Max(0f, backDepth - frontDepth) * 0.25f);
            float minDepth = frontDepth + depthMargin;
            float maxDepth = backDepth - depthMargin;
            float fallbackSpeed = body != null ? body.maxSpeed * Mathf.Clamp(Plugin.FpMoveSpeed.Value, 0.3f, 1f) : 0f;
            float speed = movementSpeed > 0f ? movementSpeed : fallbackSpeed;
            depthOffset += moveZ * speed * Time.unscaledDeltaTime;
            depthOffset = Mathf.Clamp(depthOffset, minDepth - position.z, maxDepth - position.z);
            position.z += depthOffset;
            cam.transform.SetPositionAndRotation(position, rotation);
            VrSpace.SetWorldHead(position, rotation);
            worldText?.Update();
            firstPerson.AfterHead(false);

            UpdateMinimap(body);
            voxels.Update();
            extruder.Update();
            slabs.Update();
            liquids.Update();
        }

        private void ConfigureCamera(Camera cam)
        {
            if (configuredCamera != cam)
            {
                RestoreCamera();
                configuredCamera = cam;
                cameraOrthographic = cam.orthographic;
                cameraOrthographicSize = cam.orthographicSize;
                cameraFieldOfView = cam.fieldOfView;
                cameraNear = cam.nearClipPlane;
                cameraFar = cam.farClipPlane;
                cameraMask = cam.cullingMask;
            }

            cam.orthographic = false;
            cam.fieldOfView = Mathf.Clamp(Plugin.DesktopFieldOfView.Value, 40f, 120f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = Mathf.Max(10f, Plugin.FpViewDistance.Value);
            cam.cullingMask = (cameraMask | (1 << VoxelTerrain.Layer))
                              & ~(1 << DioramaFrame.Layer)
                              & ~(1 << VrUi.OverlayLayer)
                              & ~(1 << VrUi.DesktopLayer);
            if (renderCamera != cam)
            {
                if (renderCamera != null) PopupDepth.Unregister(renderCamera);
                renderCamera = cam;
                PopupDepth.Register(cam);
                PopupDepth.Invalidate();
            }
            cam.ResetProjectionMatrix();
        }

        private void CaptureCursor()
        {
            if (!cursorStateSaved)
            {
                savedLock = Cursor.lockState;
                savedCursorVisible = Cursor.visible;
                cursorStateSaved = true;
            }
            if (cursorCaptured) return;
            cursorCaptured = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void ReleaseCursor()
        {
            if (!cursorCaptured) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            cursorCaptured = false;
        }

        private void LeaveDesktopView()
        {
            projectedMovement = false;
            moveX = moveZ = 0f;
            movementSpeed = 0f;
            depthOffset = 0f;
            RestoreItemLabels();
            extruder?.SetDesktopRenderMode(false);
            RestoreRadialPosition();
            RestoreFlashlight();
            firstPerson?.SetDesktopFlashlightMode(false);
            if (renderCamera != null)
            {
                PopupDepth.Unregister(renderCamera);
                renderCamera = null;
                PopupDepth.Invalidate();
            }
            RestoreCamera();
            if (cursorStateSaved)
            {
                Cursor.lockState = savedLock;
                Cursor.visible = savedCursorVisible;
                cursorCaptured = false;
                cursorStateSaved = false;
            }
        }

        private void DisposeDesktopFeatures()
        {
            LeaveDesktopView();
            firstPerson?.Destroy();
            firstPerson = null;
            voxels?.Destroy();
            voxels = null;
            extruder?.Destroy();
            extruder = null;
            slabs?.Destroy();
            slabs = null;
            liquids?.Destroy();
            liquids = null;
        }

        private void RestoreCamera()
        {
            if (configuredCamera == null) return;
            configuredCamera.orthographic = cameraOrthographic;
            configuredCamera.fieldOfView = cameraFieldOfView;
            configuredCamera.nearClipPlane = cameraNear;
            configuredCamera.farClipPlane = cameraFar;
            configuredCamera.cullingMask = cameraMask;
            configuredCamera.ResetProjectionMatrix();
            configuredCamera = null;
        }

        private static bool PhysicalKey(KeyCode key)
        {
            physicalInputBypass = true;
            try { return Input.GetKey(key); }
            finally { physicalInputBypass = false; }
        }

        private static bool IsCrouchKey(KeyCode key) => key == KeyBinds.GetBind("down");

        private static Vector3 PhysicalMousePosition()
        {
            physicalInputBypass = true;
            try { return Input.mousePosition; }
            finally { physicalInputBypass = false; }
        }

        private static float WorldAimDepth(Camera cam, Vector2 screen)
        {
            if (aimDepthFrame == Time.frameCount && aimDepthCamera == cam && aimDepthScreen == screen)
                return aimDepth;

            aimDepthFrame = Time.frameCount;
            aimDepthCamera = cam;
            aimDepthScreen = screen;
            aimDepth = Mathf.Min(10f, cam.farClipPlane * 0.5f);

            var world = WorldGeneration.world;
            bool checkBlocks = Plugin.VoxelsOn.Value && world != null && !world.generatingWorld;
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            Ray ray = cam.ScreenPointToRay(screen);
            float max = Mathf.Min(Mathf.Min(cam.farClipPlane, Plugin.FpViewDistance.Value), Body.interactionRange);
            float front = VoxelTerrain.FrontZ - 0.05f * PopupDepth.Scale;
            float back = VoxelTerrain.BackZ + 0.05f * PopupDepth.Scale;
            for (float distance = 0.1f; distance <= max; distance += 0.2f)
            {
                Vector3 point = ray.GetPoint(distance);
                if (point.z < front || point.z > back) continue;

                Collider2D hit = Physics2D.OverlapPoint(point, AimLayerMask);
                bool validCollider = hit != null && !IsOwnCollider(hit, body);
                if (validCollider && IsUsableTarget(hit))
                {
                    SetAimDepth(cam, point);
                    break;
                }

                if (checkBlocks && world.GetBlock((Vector2)point) != 0)
                {
                    SetAimDepth(cam, point);
                    break;
                }

                if (validCollider)
                {
                    SetAimDepth(cam, point);
                    break;
                }
            }
            return aimDepth;
        }

        private static void SetAimDepth(Camera cam, Vector3 point)
        {
            aimDepth = Mathf.Max(cam.nearClipPlane,
                Vector3.Dot(point - cam.transform.position, cam.transform.forward));
        }

        private static bool IsUsableTarget(Collider2D collider) =>
            collider.GetComponentInParent<BuildingEntity>() != null
            || collider.GetComponentInParent<UsableObject>() != null;

        private static bool IsOwnCollider(Collider2D collider, Body body)
        {
            if (body == null) return false;
            if (collider.TryGetComponent<Limb>(out var limb)) return limb.body == body;
            return collider.GetComponentInParent<Body>() == body;
        }

        private void UpdateMinimap(Body body)
        {
            if (!Plugin.DesktopMinimap.Value)
            {
                if (minimapTexture != null || minimapPixels != null) ReleaseMinimap();
                return;
            }

            var world = WorldGeneration.world;
            if (world == null || body == null || world.generatingWorld)
            {
                minimapNeedsRefresh = true;
                return;
            }

            if (minimapTexture == null)
            {
                minimapTexture = new Texture2D(MinimapWidth, MinimapHeight, TextureFormat.RGBA32, false)
                {
                    name = "CasualtyVR.DesktopMinimap",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave
                };
                minimapPixels = new Color32[MinimapWidth * MinimapHeight];
                minimapNeedsRefresh = true;
            }

            Vector2Int center = world.WorldToBlockPos(body.transform.position);
            if (!minimapNeedsRefresh && world == minimapWorld && center == minimapCenter
                && Time.unscaledTime < nextMinimapRefresh)
                return;

            minimapWorld = world;
            minimapCenter = center;
            minimapNeedsRefresh = false;
            nextMinimapRefresh = Time.unscaledTime + 2f;
            int startX = center.x - MinimapWidth / 2;
            int startY = center.y - MinimapHeight / 2;
            var empty = new Color32(12, 16, 20, 230);
            var solid = new Color32(156, 164, 170, 255);
            for (int y = 0; y < MinimapHeight; y++)
            {
                for (int x = 0; x < MinimapWidth; x++)
                {
                    minimapPixels[y * MinimapWidth + x] =
                        world.GetBlock(new Vector2Int(startX + x, startY + y)) == 0 ? empty : solid;
                }
            }
            minimapTexture.SetPixels32(minimapPixels);
            minimapTexture.Apply(false, false);
        }

        private void ReleaseMinimap()
        {
            if (minimapTexture != null) Destroy(minimapTexture);
            minimapTexture = null;
            minimapPixels = null;
            minimapWorld = null;
            minimapNeedsRefresh = true;
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            Canvas.willRenderCanvases -= CenterInventoryRadial;
            RestoreItemLabels();
            if (itemLabelCanvas != null) Destroy(itemLabelCanvas.gameObject);
            itemLabelCanvas = null;
            itemLabelCanvasRect = null;
            itemLabelCanvasScaler = null;
            RestoreRadialPosition();
            RestoreFlashlight();
            worldText?.Destroy();
            worldText = null;
            DisposeDesktopFeatures();
            ReleaseMinimap();
            if (instance == this) instance = null;
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (cam != renderCamera || !FirstPerson.Active) return;
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            var heldItem = body != null ? body.GetItem(body.handSlot) : null;
            var lightItem = heldItem != null ? heldItem.GetComponentInChildren<LightItem>(true) : null;
            var light = lightItem != null ? lightItem.light : null;
            if (light == null || !light.enabled)
            {
                ShaderFog.SetFlashlightGlobals(Vector3.zero, Vector3.forward, 0f, 0f, 0f, 0f, Color.white);
                return;
            }
            disabledFlashlights.Add((light, light.enabled));
            light.enabled = false;
            float range = Mathf.Clamp(light.pointLightOuterRadius * 2.5f, 4f, 24f);
            ShaderFog.SetFlashlightGlobals(cam.transform.position, cam.transform.forward, range, 14f, 22f, 1.8f, Color.white);
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (cam == renderCamera) RestoreFlashlight();
        }

        private void RestoreFlashlight()
        {
            for (int i = disabledFlashlights.Count - 1; i >= 0; i--)
            {
                var entry = disabledFlashlights[i];
                if (entry.light != null) entry.light.enabled = entry.enabled;
            }
            disabledFlashlights.Clear();
            ShaderFog.SetFlashlightGlobals(Vector3.zero, Vector3.forward, 0f, 0f, 0f, 0f, Color.white);
        }

        private void CenterInventoryRadial()
        {
            var pc = PlayerCamera.main;
            var radial = renderCamera != null && FirstPerson.Active && pc != null
                && !Active(pc.woundView) && !Active(pc.tradeMenu) && pc.radialMenu != null
                && pc.radialMenu.gameObject.activeInHierarchy
                && (pc.radialOpen || centeredRadial == pc.radialMenu)
                ? pc.radialMenu
                : null;
            if (radial != centeredRadial)
            {
                RestoreRadialPosition();
                if (radial != null)
                {
                    centeredRadial = radial;
                    radialParent = radial.parent as RectTransform;
                    radialCanvas = radial.GetComponentInParent<Canvas>();
                    radialOriginalPosition = radial.anchoredPosition;
                    radialLastPosition = radial.anchoredPosition;
                }
            }
            if (radial == null || radialParent == null || radialCanvas == null) return;

            if ((radial.anchoredPosition - radialLastPosition).sqrMagnitude > 0.01f)
                radialOriginalPosition = radial.anchoredPosition;

            radial.GetComponentsInChildren<Graphic>(false, radialGraphics);
            bool any = false;
            float xMin = float.PositiveInfinity, yMin = float.PositiveInfinity;
            float xMax = float.NegativeInfinity, yMax = float.NegativeInfinity;
            Camera eventCamera = radialCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : radialCanvas.worldCamera;
            foreach (var graphic in radialGraphics)
            {
                if (graphic == null || !graphic.gameObject.activeInHierarchy || !(graphic.transform is RectTransform rect)) continue;
                rect.GetWorldCorners(radialCorners);
                Vector2 first = RectTransformUtility.WorldToScreenPoint(eventCamera, radialCorners[0]);
                float partXMin = first.x, partXMax = first.x, partYMin = first.y, partYMax = first.y;
                for (int i = 1; i < radialCorners.Length; i++)
                {
                    Vector2 corner = RectTransformUtility.WorldToScreenPoint(eventCamera, radialCorners[i]);
                    partXMin = Mathf.Min(partXMin, corner.x);
                    partXMax = Mathf.Max(partXMax, corner.x);
                    partYMin = Mathf.Min(partYMin, corner.y);
                    partYMax = Mathf.Max(partYMax, corner.y);
                }
                if (partXMax - partXMin > Screen.width * 0.6f || partYMax - partYMin > Screen.height * 0.9f) continue;
                xMin = Mathf.Min(xMin, partXMin);
                xMax = Mathf.Max(xMax, partXMax);
                yMin = Mathf.Min(yMin, partYMin);
                yMax = Mathf.Max(yMax, partYMax);
                any = true;
            }
            if (!any) return;

            Vector2 boundsCenter = new Vector2((xMin + xMax) * 0.5f, (yMin + yMax) * 0.5f);
            Vector2 targetCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(radialParent, boundsCenter, eventCamera, out var currentLocal)
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(radialParent, targetCenter, eventCamera, out var targetLocal))
                return;
            radial.anchoredPosition += targetLocal - currentLocal;
            radialLastPosition = radial.anchoredPosition;
        }

        private void RestoreRadialPosition()
        {
            if (centeredRadial != null)
                centeredRadial.anchoredPosition = radialOriginalPosition;
            centeredRadial = null;
            radialParent = null;
            radialCanvas = null;
            radialOriginalPosition = Vector2.zero;
            radialLastPosition = Vector2.zero;
        }

        private static bool Active(GameObject go) => go != null && go.activeInHierarchy;

        [HarmonyPatch(typeof(WoundView), "UpdateView")]
        private static class WoundViewFadePatch
        {
            private static void Postfix(WoundView __instance)
            {
                if (renderCamera == null || !FirstPerson.Active || (VrRig.Instance != null && VrRig.Instance.Active)
                    || __instance == null || __instance.body == null || __instance.body.limbs == null || __instance.limbImages == null)
                    return;

                var canvasRect = __instance.GetComponentInParent<Canvas>()?.transform as RectTransform;
                Rect canvasBounds = canvasRect != null ? canvasRect.rect : default;
                for (int i = 0; i < __instance.limbImages.Length && i < __instance.body.limbs.Length; i++)
                {
                    var image = __instance.limbImages[i];
                    if (image == null || image.rectTransform == null) continue;

                    float y01 = canvasRect != null && canvasBounds.height > 0f
                        ? Mathf.InverseLerp(canvasBounds.yMin, canvasBounds.yMax, canvasRect.InverseTransformPoint(image.rectTransform.position).y)
                        : image.rectTransform.position.y / Mathf.Max(1f, Screen.height);
                    float alpha = ((__instance.timeOpen + 0.15f) * 12f
                        - y01 * 1080f * 0.01f) * 0.25f;
                    if (__instance.body.limbs[i] == null || __instance.body.limbs[i].dismembered) alpha = 0f;

                    Color color = image.color;
                    color.a = alpha;
                    image.color = color;
                    if (image.transform.childCount == 0) continue;
                    var inner = image.transform.GetChild(0).GetComponent<UnityEngine.UI.Image>();
                    if (inner == null) continue;
                    color = inner.color;
                    color.a = alpha;
                    inner.color = color;
                }
            }
        }

        [HarmonyPatch(typeof(AltHoverScript), "Update")]
        private static class ItemLabelCenterPatch
        {
            private static readonly AccessTools.FieldRef<AltHoverScript, System.Collections.Generic.List<ItemLabel>> ActiveLabels =
                AccessTools.FieldRefAccess<AltHoverScript, System.Collections.Generic.List<ItemLabel>>("activeLabels");
            private static readonly System.Collections.Generic.List<ItemLabel> LayoutLabels =
                new System.Collections.Generic.List<ItemLabel>();
            private static readonly System.Collections.Generic.List<Rect> OccupiedRects =
                new System.Collections.Generic.List<Rect>();

            private static void Postfix(AltHoverScript __instance)
            {
                if (renderCamera == null || !FirstPerson.Active || (VrRig.Instance != null && VrRig.Instance.Active))
                {
                    RestoreItemLabels();
                    return;
                }

                var labels = ActiveLabels(__instance);
                if (labels.Count == 0)
                {
                    RestoreItemLabels();
                    return;
                }
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                float orthographicSize = instance != null ? instance.cameraOrthographicSize : 0f;
                if (body == null || orthographicSize <= 0f)
                {
                    RestoreItemLabels();
                    return;
                }

                Canvas rootCanvas = null;
                LayoutLabels.Clear();
                foreach (var label in labels)
                {
                    if (label == null || label.rect == null || label.refItem == null || !label.gameObject.activeInHierarchy) continue;
                    Canvas canvas = ItemLabelLayouts.TryGetValue(label, out ItemLabelLayout savedLayout)
                        && savedLayout.Parent != null
                        ? savedLayout.Parent.GetComponentInParent<Canvas>()
                        : label.rect.GetComponentInParent<Canvas>();
                    if (canvas == null) continue;
                    if (rootCanvas == null) rootCanvas = canvas.rootCanvas;
                    if (canvas.rootCanvas != rootCanvas) continue;
                    LayoutLabels.Add(label);
                }

                if (rootCanvas == null || LayoutLabels.Count == 0) return;
                EnsureItemLabelCanvas(rootCanvas);
                if (itemLabelCanvasRect == null || itemLabelCanvasScaler == null) return;

                foreach (var label in LayoutLabels)
                {
                    if (!ItemLabelLayouts.ContainsKey(label))
                    {
                        var rect = label.rect;
                        ItemLabelLayouts.Add(label, new ItemLabelLayout
                        {
                            Parent = rect.parent,
                            SiblingIndex = rect.GetSiblingIndex(),
                            AnchorMin = rect.anchorMin,
                            AnchorMax = rect.anchorMax,
                            Pivot = rect.pivot,
                            SizeDelta = rect.sizeDelta,
                            LocalScale = rect.localScale,
                            LocalRotation = rect.localRotation
                        });
                    }
                }

                foreach (var label in LayoutLabels)
                {
                    if (label.rect.parent != itemLabelCanvasRect)
                    {
                        label.rect.SetParent(itemLabelCanvasRect, false);
                        label.rect.anchorMin = label.rect.anchorMax = new Vector2(0.5f, 0.5f);
                        label.rect.pivot = new Vector2(0.5f, 0.5f);
                        label.rect.localScale = Vector3.one;
                        label.rect.localRotation = Quaternion.identity;
                    }
                }

                itemLabelCanvasScaler.scaleFactor = Mathf.Max(0.01f, rootCanvas.scaleFactor);
                Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                float pixelsPerUnit = Screen.height / (2f * orthographicSize);
                RectTransform canvasRect = itemLabelCanvasRect;
                foreach (var label in LayoutLabels)
                {
                    if (label.refItem == null) continue;
                    Vector3 delta = label.refItem.transform.position - body.transform.position;
                    Vector2 itemScreen = screenCenter + new Vector2(delta.x, delta.y) * pixelsPerUnit;
                    if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, itemScreen, null, out Vector2 local))
                        label.rect.anchoredPosition = local;
                }

                LayoutLabels.Sort((a, b) =>
                {
                    int y = b.rect.anchoredPosition.y.CompareTo(a.rect.anchoredPosition.y);
                    return y != 0 ? y : a.rect.anchoredPosition.x.CompareTo(b.rect.anchoredPosition.x);
                });

                OccupiedRects.Clear();
                foreach (var label in LayoutLabels)
                {
                    Vector2 position = label.rect.anchoredPosition;
                    position.x = Mathf.Round(position.x);
                    position.y = Mathf.Round(position.y);
                    Rect rect = label.GetRect(position);
                    bool overlaps;
                    do
                    {
                        overlaps = false;
                        foreach (Rect occupied in OccupiedRects)
                        {
                            if (!rect.Overlaps(occupied)) continue;
                            position.y = occupied.yMin - rect.height - 4f / itemLabelCanvasScaler.scaleFactor
                                         + rect.height * 0.5f;
                            rect = label.GetRect(position);
                            overlaps = true;
                        }
                    }
                    while (overlaps);

                    label.rect.anchoredPosition = position;
                    OccupiedRects.Add(rect);
                }
            }
        }

        private static void EnsureItemLabelCanvas(Canvas sourceCanvas)
        {
            if (itemLabelCanvas == null)
            {
                var go = new GameObject("CasualtyVR.ItemLabels", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                DontDestroyOnLoad(go);
                itemLabelCanvas = go.GetComponent<Canvas>();
                itemLabelCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                itemLabelCanvas.overrideSorting = true;
                itemLabelCanvasRect = go.transform as RectTransform;
                itemLabelCanvasScaler = go.GetComponent<CanvasScaler>();
                itemLabelCanvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                itemLabelCanvasScaler.scaleFactor = Mathf.Max(0.01f, sourceCanvas.scaleFactor);
            }

            itemLabelCanvas.sortingLayerID = sourceCanvas.sortingLayerID;
            itemLabelCanvas.sortingOrder = sourceCanvas.sortingOrder + 1;
        }

        private static void RestoreItemLabels()
        {
            ItemLabelScratch.Clear();
            ItemLabelScratch.AddRange(ItemLabelLayouts.Keys);
            foreach (var label in ItemLabelScratch)
            {
                if (label == null)
                {
                    ItemLabelLayouts.Remove(label);
                    continue;
                }

                ItemLabelLayout layout = ItemLabelLayouts[label];
                RectTransform rect = label.rect;
                Vector3 worldPosition = rect.position;
                rect.SetParent(layout.Parent, true);
                rect.anchorMin = layout.AnchorMin;
                rect.anchorMax = layout.AnchorMax;
                rect.pivot = layout.Pivot;
                rect.sizeDelta = layout.SizeDelta;
                rect.localScale = layout.LocalScale;
                rect.localRotation = layout.LocalRotation;
                rect.position = worldPosition;
                if (layout.Parent != null)
                    rect.SetSiblingIndex(Mathf.Clamp(layout.SiblingIndex, 0, layout.Parent.childCount - 1));
                ItemLabelLayouts.Remove(label);
            }
            ItemLabelScratch.Clear();
        }

        private static bool IsInventoryOpen()
        {
            var pc = PlayerCamera.main;
            return pc != null && (pc.radialOpen || (pc.radialMenu != null && pc.radialMenu.gameObject.activeInHierarchy));
        }

        private void OnGUI()
        {
            if (!DesktopInputActive || VrUi.GameMenuOpen() || Event.current.type != EventType.Repaint) return;
            float x = Screen.width * 0.5f, y = Screen.height * 0.5f;
            Color previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.85f);
            GUI.DrawTexture(new Rect(x - 5f, y - 1f, 10f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(x - 1f, y - 5f, 2f, 10f), Texture2D.whiteTexture);
            if (Plugin.DesktopMinimap.Value && minimapTexture != null)
            {
                const float width = 256f, height = 128f, margin = 16f;
                var rect = new Rect(Screen.width - width - margin, margin, width, height);
                GUI.color = new Color(0f, 0f, 0f, 0.75f);
                GUI.DrawTexture(new Rect(rect.x - 3f, rect.y - 3f, rect.width + 6f, rect.height + 6f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.DrawTexture(rect, minimapTexture);
                GUI.color = new Color(0.35f, 1f, 0.45f, 1f);
                GUI.DrawTexture(new Rect(rect.center.x - 3f, rect.center.y - 3f, 6f, 6f), Texture2D.whiteTexture);
            }
            GUI.color = previous;
        }

        [System.ThreadStatic] private static bool physicalInputBypass;

        [HarmonyPatch(typeof(Input), nameof(Input.mousePosition), MethodType.Getter)]
        private static class MousePositionPatch
        {
            private static void Postfix(ref Vector3 __result)
            {
                if (physicalInputBypass || !DesktopInputActive || renderCamera == null) return;
                Vector3 mouse = PhysicalMousePosition();
                if (instance.cursorCaptured)
                {
                    mouse.x = Screen.width * 0.5f;
                    mouse.y = Screen.height * 0.5f;
                }
                float depth = WorldAimDepth(renderCamera, new Vector2(mouse.x, mouse.y));
                __result = new Vector3(mouse.x, mouse.y, depth);
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetKey), typeof(KeyCode))]
        private static class KeyboardHeldPatch
        {
            private static void Postfix(KeyCode key, ref bool __result)
            {
                if (physicalInputBypass || !DesktopInputActive) return;
                if (IsCrouchKey(key)) { __result = instance.keyCrouch; return; }
                var forwardKey = KeyBinds.GetBind(FirstPerson.FacingRightNow ? "right" : "left");
                var backKey = KeyBinds.GetBind(FirstPerson.FacingRightNow ? "left" : "right");
                if (instance.projectedMovement && (key == forwardKey || key == backKey))
                    __result = key == forwardKey ? instance.keyForward : instance.keyBack;
                else if (!__result)
                {
                    if (key == forwardKey) __result = instance.keyForward;
                    else if (key == backKey) __result = instance.keyBack;
                    else if (key == KeyBinds.GetBind("jump")) __result = instance.keyJump;
                }
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), typeof(KeyCode))]
        private static class KeyboardDownPatch
        {
            private static void Postfix(KeyCode key, ref bool __result)
            {
                if (physicalInputBypass || !DesktopInputActive) return;
                if (IsCrouchKey(key)) { __result = instance.downCrouch; return; }
                var forwardKey = KeyBinds.GetBind(FirstPerson.FacingRightNow ? "right" : "left");
                var backKey = KeyBinds.GetBind(FirstPerson.FacingRightNow ? "left" : "right");
                if (instance.projectedMovement && (key == forwardKey || key == backKey))
                    __result = key == forwardKey ? instance.downForward : instance.downBack;
                else if (!__result)
                {
                    if (key == forwardKey) __result = instance.downForward;
                    else if (key == backKey) __result = instance.downBack;
                    else if (key == KeyBinds.GetBind("jump")) __result = instance.downJump;
                }
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), typeof(KeyCode))]
        private static class KeyboardUpPatch
        {
            private static void Postfix(KeyCode key, ref bool __result)
            {
                if (physicalInputBypass || !DesktopInputActive) return;
                if (IsCrouchKey(key)) { __result = instance.upCrouch; return; }
                var forwardKey = KeyBinds.GetBind(FirstPerson.FacingRightNow ? "right" : "left");
                var backKey = KeyBinds.GetBind(FirstPerson.FacingRightNow ? "left" : "right");
                if (instance.projectedMovement && (key == forwardKey || key == backKey))
                    __result = key == forwardKey ? instance.upForward : instance.upBack;
                else if (!__result)
                {
                    if (key == forwardKey) __result = instance.upForward;
                    else if (key == backKey) __result = instance.upBack;
                    else if (key == KeyBinds.GetBind("jump")) __result = instance.upJump;
                }
            }
        }
    }
}
