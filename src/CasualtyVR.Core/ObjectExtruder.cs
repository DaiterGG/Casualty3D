using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CasualtyVR
{
    /// <summary>
    /// Phase 4: real thickness for static world objects (user request 2026-10-04: "drop capsules ... and similar
    /// objects extrude"). Every visible BuildingEntity sprite (drop capsules, crates, doors, lamps, machines)
    /// with its own Collider2D gets a mesh extruded from the sprite's own pixels: the sprite on a front and a back
    /// face, and a side wall along every edge between a solid and a see-through pixel (shaded like the voxel
    /// blocks). The mesh is cached per sprite (doors and openables swap sprites) and follows the transform. The
    /// original sprite is hidden only while an eye renders; the monitor is untouched. Sprites attached to the
    /// object (the capsule's glowing emission panel) are placed on the front face by PopupDepth.
    /// </summary>
    internal sealed class ObjectExtruder
    {
        private sealed class Entry
        {
            public SpriteRenderer Source;
            public BuildingEntity Building;
            public GameObject Go, DepthGo;
            public MeshFilter Filter;
            public MeshRenderer Renderer, DepthRenderer;
            public Sprite Built;
            public bool IsBox;
            public bool IsCrystal;
            public float WorldZ;
            public bool DesktopHidden, OriginalForceOff;
        }

        private static readonly Dictionary<Sprite, Mesh> meshes = new Dictionary<Sprite, Mesh>();
        private static readonly Dictionary<Sprite, Mesh> boxMeshes = new Dictionary<Sprite, Mesh>();
        private static readonly Dictionary<Texture, Material> depthMaterials = new Dictionary<Texture, Material>();
        private static Material opaqueBoxDepthMaterial;
        private static readonly HashSet<Sprite> failed = new HashSet<Sprite>();
        internal static readonly HashSet<SpriteRenderer> Extruded = new HashSet<SpriteRenderer>();

        private readonly GameObject root;
        private readonly Dictionary<SpriteRenderer, Entry> entries = new Dictionary<SpriteRenderer, Entry>();
        private readonly List<SpriteRenderer> hidden = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> stale = new List<SpriteRenderer>();
        private readonly MaterialPropertyBlock props = new MaterialPropertyBlock();
        private readonly Shader depthShader;
        private int lastScan = -1000;

        public int Count => entries.Count;

        /// <summary>Half the object depth in world units: the front face is at -Half, the back at +Half.</summary>
        public static float Half => Plugin.ObjectDepth.Value * 0.5f * PopupDepth.Scale;
        public static bool On => Plugin.ObjectDepthOn.Value && Plugin.VoxelsOn.Value && PopupDepth.Scale > 0f;

        public static float DepthFor(BuildingEntity building)
        {
            string id = building != null ? building.id : null;
            bool thinDetail = HasToken(id, "button") || HasToken(id, "vent") || HasToken(id, "shutter");
            return Plugin.ObjectDepth.Value * (thinDetail ? 0.2f : 1f);
        }

        private static float DepthFor(BuildingEntity building, SpriteRenderer source)
        {
            if (IsPodMonitor(building, source)) return Plugin.ObjectDepth.Value * 0.2f;
            return DepthFor(building);
        }

        internal static float DecorationDepthFor(BuildingEntity building, SpriteRenderer source)
        {
            if (IsPodMonitor(building, source)) return DepthFor(building, source);
            if (building != null)
            {
                foreach (var child in building.GetComponentsInChildren<SpriteRenderer>(true))
                    if (child != source && Extruded.Contains(child) && IsPodMonitor(building, child))
                        return DepthFor(building, child);
            }
            return DepthFor(building);
        }

        public ObjectExtruder()
        {
            root = new GameObject("CasualtyVR.Extruded") { hideFlags = HideFlags.DontSave };
            Object.DontDestroyOnLoad(root);
            depthShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (depthShader == null) Plugin.Log.LogWarning("Object depth: URP Particles/Unlit not found; extruded objects will not occlude sprites");
            RenderPipelineManager.beginCameraRendering += OnBegin;
            RenderPipelineManager.endCameraRendering += OnEnd;
        }

        public void Update()
        {
            bool on = On;
            if (root.activeSelf != on) root.SetActive(on);
            if (!on) { if (entries.Count > 0) ClearAll(); return; }
            if (Scans.Due(lastScan, 30, 10)) { long t0 = Scans.Start(); Rescan(); Scans.Stop("object depth", t0); }

            stale.Clear();
            foreach (var kv in entries)
            {
                var src = kv.Key;
                var e = kv.Value;
                if (src == null) { stale.Add(src); continue; }
                bool show = src.enabled && src.gameObject.activeInHierarchy && src.sprite != null;
                if (e.Go.activeSelf != show) e.Go.SetActive(show);
                if (e.DepthGo != null && e.DepthGo.activeSelf != show) e.DepthGo.SetActive(show);
                if (!show) continue;
                if (e.Built != src.sprite)
                {
                    var mesh = e.IsBox ? BoxMeshFor(src.sprite) : MeshFor(src.sprite);
                    if (mesh == null && (e.Building != null || e.IsCrystal))
                    {
                        failed.Remove(src.sprite);
                        mesh = PackedMeshFor(src.sprite);
                    }
                    if (mesh == null) { stale.Add(src); continue; }
                    e.Filter.sharedMesh = mesh;
                    if (e.DepthGo != null) e.DepthGo.GetComponent<MeshFilter>().sharedMesh = mesh;
                    if (depthShader != null) e.DepthRenderer.sharedMaterial = e.IsBox
                        ? BoxDepthMaterial()
                        : DepthMaterial(src.sprite.texture);
                    e.Built = src.sprite;
                }
                var t = src.transform;
                var scale = t.lossyScale;
                scale.x *= src.flipX ? -1f : 1f;
                scale.y *= src.flipY ? -1f : 1f;
                Vector3 pos = new Vector3(t.position.x, t.position.y, e.WorldZ);
                if (e.IsBox)
                {
                    Vector2 bounds = src.sprite.bounds.size;
                    float side = Mathf.Max(bounds.x * Mathf.Abs(scale.x), bounds.y * Mathf.Abs(scale.y));
                    scale.x = Mathf.Sign(scale.x) * side;
                    scale.y = Mathf.Sign(scale.y) * side;
                    scale.z = side;
                    Vector3 center = src.sprite.bounds.center;
                    center.x *= src.flipX ? -1f : 1f;
                    center.y *= src.flipY ? -1f : 1f;
                    pos = t.TransformPoint(center);
                    pos.z = e.WorldZ;
                }
                bool thinPlant = FirstPerson.Active && IsThinPlant(e.Building, src);
                bool centeredCrystal = FirstPerson.Active && e.IsCrystal;
                float depth = DepthFor(e.Building, src) * (thinPlant ? 0.2f : 1f);
                if (!e.IsBox) scale.z = PopupDepth.Scale > 0f ? depth * PopupDepth.Scale : 0.001f;   // mesh depth is 1
                if (FirstPerson.Active && IsSteelDoor(e.Building, src))
                {
                    float currentWidth = src.sprite.bounds.size.x * Mathf.Abs(scale.x);
                    float targetWidth = Mathf.Max(0.1f, FirstPerson.HalfWidth * 2f - 0.2f);
                    if (currentWidth > 0.001f) scale.x *= targetWidth / currentWidth;
                }
                else if (FirstPerson.Active && IsThinDetail(e.Building, src))
                {
                    if (IsButton(e.Building, src))
                    {
                        float width = src.sprite.bounds.size.x;
                        float height = src.sprite.bounds.size.y;
                        if (width > 0.001f && height > 0.001f)
                            scale.x = Mathf.Sign(scale.x) * Mathf.Abs(scale.y) * height / width;
                    }
                    else scale.x *= 0.2f;
                }
                // First person: objects of the background bands (machines set into walls) stand against the back wall.
                float halfDepth = depth * PopupDepth.Scale * 0.5f;
                bool steelDoor = IsSteelDoor(e.Building, src);
                bool foregroundDoor = steelDoor || IsDoorOrGate(e.Building, src);
                bool backWallObject = src.sortingOrder <= -100 && !foregroundDoor && !thinPlant && !centeredCrystal;
                float z = FirstPerson.Active && backWallObject ? VoxelTerrain.BackZ - halfDepth - 0.02f * PopupDepth.Scale : 0f;
                // First person: objects in the tunnel (plants, crates, doors, capsules) turn to face you, keeping their depth.
                if (!e.IsBox)
                {
                    pos = new Vector3(t.position.x, t.position.y, z);
                    if (!backWallObject) pos.z = e.WorldZ;
                }
                Quaternion face = !backWallObject && !e.IsBox
                    ? FirstPerson.FaceYaw(pos, lockDesktopDepth: true)
                    : Quaternion.identity;
                Quaternion objectRotation = centeredCrystal ? face : face * t.rotation;
                e.Go.transform.SetPositionAndRotation(pos, objectRotation);
                e.Go.transform.localScale = steelDoor ? scale * 0.99f : scale;
                if (e.DepthGo != null)
                {
                    e.DepthGo.transform.SetPositionAndRotation(e.Go.transform.position, e.Go.transform.rotation);
                    e.DepthGo.transform.localScale = e.Go.transform.localScale;
                }
                if (e.Renderer.sharedMaterial != src.sharedMaterial) e.Renderer.sharedMaterial = src.sharedMaterial;
                e.Renderer.sortingLayerID = src.sortingLayerID;
                e.Renderer.sortingOrder = FirstPerson.Active && steelDoor ? Mathf.Max(src.sortingOrder, -99) : src.sortingOrder;
                if (e.DepthRenderer != null)
                {
                    e.DepthRenderer.sortingLayerID = e.Renderer.sortingLayerID;
                    e.DepthRenderer.sortingOrder = e.Renderer.sortingOrder;
                }
                props.Clear();
                props.SetTexture("_MainTex", src.sprite.texture);
                props.SetColor("_RendererColor", src.color);
                e.Renderer.SetPropertyBlock(props);
            }
            SetDesktopRenderMode(DesktopFirstPerson.IsDesktopRenderActive);
            foreach (var s in stale) Remove(s);
        }

        public void SetDesktopRenderMode(bool active)
        {
            foreach (var entry in entries.Values)
            {
                var src = entry.Source;
                if (src == null) continue;
                if (active && !entry.DesktopHidden)
                {
                    entry.OriginalForceOff = src.forceRenderingOff;
                    src.forceRenderingOff = true;
                    entry.DesktopHidden = true;
                }
                else if (!active && entry.DesktopHidden)
                {
                    src.forceRenderingOff = entry.OriginalForceOff;
                    entry.DesktopHidden = false;
                }
            }
        }

        private void Rescan()
        {
            lastScan = Time.frameCount;
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            foreach (var sr in Object.FindObjectsOfType<SpriteRenderer>())
            {
                if (sr == null || entries.ContainsKey(sr) || sr.sprite == null) continue;
                var building = sr.GetComponentInParent<BuildingEntity>();
                var item = sr.GetComponentInParent<Item>();
                bool box = IsBox(building) || IsBox(item);
                bool plant = IsThinPlant(building, sr);
                bool steelDoor = IsSteelDoor(building, sr);
                bool doorOrGate = IsDoorOrGate(building, sr);
                if (building == null && !box && !plant && !steelDoor && !doorOrGate) continue;
                if (failed.Contains(sr.sprite) && building == null && !plant && !steelDoor && !doorOrGate) continue;
                if (!sr.isVisible || sr.drawMode != SpriteDrawMode.Simple) continue;
                if (building == null && item != null && item.GetComponent<SpriteRenderer>() != sr) continue;
                if (item != null && body != null && item.transform.IsChildOf(body.transform)) continue;
                var collider = building != null ? building.GetComponent<Collider2D>()
                    : item != null ? item.GetComponent<Collider2D>() : null;
                if (collider == null && building != null) collider = building.GetComponentInChildren<Collider2D>();
                if (collider == null && item != null) collider = item.GetComponentInChildren<Collider2D>();
                if (collider == null && building == null && !plant && !steelDoor && !doorOrGate) continue;
                var r = sr.sprite.rect;
                if (r.width * r.height > 256 * 256) continue;           // huge backdrops stay flat
                string objectName = building != null ? building.name : item != null ? item.name : sr.name;
                var go = new GameObject("extruded " + objectName) { layer = VoxelTerrain.Layer, hideFlags = HideFlags.DontSave };
                go.transform.SetParent(root.transform, false);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                GameObject depthGo = null;
                MeshRenderer depthRenderer = null;
                if (depthShader != null)
                {
                    depthGo = new GameObject("depth " + objectName) { layer = VoxelTerrain.Layer, hideFlags = HideFlags.DontSave };
                    depthGo.transform.SetParent(root.transform, false);
                    depthGo.AddComponent<MeshFilter>();
                    depthRenderer = depthGo.AddComponent<MeshRenderer>();
                    depthRenderer.shadowCastingMode = ShadowCastingMode.Off;
                    depthRenderer.receiveShadows = false;
                    depthRenderer.lightProbeUsage = LightProbeUsage.Off;
                    depthRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    depthRenderer.sharedMaterial = box ? BoxDepthMaterial() : DepthMaterial(sr.sprite.texture);
                }
                entries[sr] = new Entry
                {
                    Source = sr, Building = building, Go = go, DepthGo = depthGo, Filter = mf, Renderer = mr,
                    DepthRenderer = depthRenderer, IsBox = box,
                    IsCrystal = IsCrystal(building), WorldZ = 0f
                };
                Extruded.Add(sr);
            }
        }

        private static bool IsBox(BuildingEntity building) =>
            building != null && (IsBoxName(building.id) || IsBoxName(building.name));

        private static bool IsBox(Item item) =>
            item != null && (IsBoxName(item.id) || IsBoxName(item.name));

        private static bool IsBoxName(string value) =>
            HasToken(value, "box") || HasToken(value, "crate");

        private Material DepthMaterial(Texture texture)
        {
            if (depthMaterials.TryGetValue(texture, out var material) && material != null) return material;
            material = MakeDepthMaterial(texture, "CasualtyVR.ObjectDepth " + texture.name, alphaClip: true);
            depthMaterials[texture] = material;
            return material;
        }

        private Material BoxDepthMaterial()
        {
            if (opaqueBoxDepthMaterial != null) return opaqueBoxDepthMaterial;
            opaqueBoxDepthMaterial = MakeDepthMaterial(Texture2D.whiteTexture, "CasualtyVR.BoxDepth", alphaClip: false);
            return opaqueBoxDepthMaterial;
        }

        private Material MakeDepthMaterial(Texture texture, string name, bool alphaClip)
        {
            var material = new Material(depthShader) { name = name };
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.Zero);
            material.SetFloat("_DstBlend", (float)BlendMode.One);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.Zero);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_ZWrite", 1f);
            material.SetFloat("_Cull", 0f);
            material.SetFloat("_AlphaClip", alphaClip ? 1f : 0f);
            material.SetFloat("_Cutoff", 0.5f);
            if (alphaClip) material.EnableKeyword("_ALPHATEST_ON");
            else material.DisableKeyword("_ALPHATEST_ON");
            material.renderQueue = (int)RenderQueue.AlphaTest;
            return material;
        }

        internal static bool IsSteelDoor(BuildingEntity building)
        {
            if (building == null) return false;
            return string.Equals(building.id, "reinforceddoor", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(building.id, "shuttledoor", System.StringComparison.OrdinalIgnoreCase)
                || IsSteelGateName(building.id) || IsSteelGateName(building.name);
        }

        internal static bool IsSteelDoor(BuildingEntity building, SpriteRenderer source) =>
            IsSteelDoor(building) || IsSteelGateName(source != null ? source.name : null)
            || IsSteelGateName(source != null && source.sprite != null ? source.sprite.name : null);

        private static bool IsDoorOrGate(BuildingEntity building, SpriteRenderer source)
        {
            if (IsDoorOrGateName(building != null ? building.id : null)
                || IsDoorOrGateName(building != null ? building.name : null)
                || IsDoorOrGateName(source != null ? source.name : null)
                || IsDoorOrGateName(source != null && source.sprite != null ? source.sprite.name : null))
                return true;
            for (var t = source != null ? source.transform.parent : null; t != null; t = t.parent)
                if (IsDoorOrGateName(t.name)) return true;
            return false;
        }

        private static bool IsDoorOrGateName(string value) =>
            HasToken(value, "door") || HasToken(value, "gate");

        private static bool IsSteelGateName(string value) =>
            HasToken(value, "steelgate") || HasToken(value, "steel_gate")
            || (HasToken(value, "steel") && HasToken(value, "gate"));

        internal static bool IsCrystal(BuildingEntity building) =>
            building != null && building.GetComponent<CrystalBehaviour>() != null;

        private static bool IsThinPlant(BuildingEntity building, SpriteRenderer source)
        {
            string id = building != null ? building.id : null;
            string objectName = building != null ? building.name : null;
            string rendererName = source != null ? source.name : null;
            string spriteName = source != null && source.sprite != null ? source.sprite.name : null;
            if (HasPlantToken(id) || HasPlantToken(objectName) || HasPlantToken(rendererName) || HasPlantToken(spriteName))
                return true;
            for (var t = source != null ? source.transform.parent : null; t != null; t = t.parent)
                if (HasPlantToken(t.name)) return true;
            return false;
        }

        private static bool HasPlantToken(string value) =>
            HasToken(value, "plant") || HasToken(value, "bush") || HasToken(value, "tree")
            || HasToken(value, "shroom") || HasToken(value, "mushroom") || HasToken(value, "flower")
            || HasToken(value, "rose") || HasToken(value, "reed") || HasToken(value, "cactus")
            || HasToken(value, "rye") || HasToken(value, "glowplant") || HasToken(value, "stoneplant") || HasToken(value, "leadbush")
            || HasToken(value, "drybush") || HasToken(value, "bananaplant")
            || HasToken(value, "geotree") || HasToken(value, "glowfruit") || HasToken(value, "geofrut")
            || HasToken(value, "ceilingrye") || HasToken(value, "hydreed") || HasToken(value, "cactus")
            || HasToken(value, "sandrose") || HasToken(value, "brownshroom") || HasToken(value, "browncap")
            || HasToken(value, "grabberplant") || HasToken(value, "wallflower") || HasToken(value, "holidaytree");

        private static bool IsThinDetail(BuildingEntity building, SpriteRenderer source)
        {
            string id = building != null ? building.id : null;
            string objectName = building != null ? building.name : null;
            string rendererName = source != null ? source.name : null;
            string spriteName = source != null && source.sprite != null ? source.sprite.name : null;
            return HasToken(id, "button") || HasToken(id, "vent") || HasToken(id, "shutter") || HasToken(id, "geyser")
                || HasToken(objectName, "button") || HasToken(objectName, "vent") || HasToken(objectName, "shutter")
                || HasToken(rendererName, "button") || HasToken(rendererName, "vent") || HasToken(rendererName, "shutter")
                || HasToken(spriteName, "button") || HasToken(spriteName, "vent") || HasToken(spriteName, "shutter");
        }

        private static bool IsPodMonitor(BuildingEntity building, SpriteRenderer source)
        {
            string buildingId = building != null ? building.id : null;
            string objectName = building != null ? building.name : null;
            string rendererName = source != null ? source.name : null;
            string spriteName = source != null && source.sprite != null ? source.sprite.name : null;
            return HasToken(buildingId, "lifepodscreen") || HasToken(objectName, "lifepodscreen")
                || HasToken(rendererName, "lifepodscreen") || HasToken(spriteName, "lifepodscreen");
        }

        private static bool IsButton(BuildingEntity building, SpriteRenderer source)
        {
            string id = building != null ? building.id : null;
            string objectName = building != null ? building.name : null;
            string rendererName = source != null ? source.name : null;
            string spriteName = source != null && source.sprite != null ? source.sprite.name : null;
            return HasToken(id, "button") || HasToken(objectName, "button")
                || HasToken(rendererName, "button") || HasToken(spriteName, "button");
        }

        private static bool HasToken(string value, string token) =>
            !string.IsNullOrEmpty(value) && value.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0;

        // ---- mesh from the sprite's pixels ------------------------------------------------------------

        /// <summary>
        /// Mesh in the sprite's local space (units = sprite units, z from -0.5 to 0.5): back face, side walls,
        /// front face, in that order (painter's order; sprite shaders do not write depth).
        /// </summary>
        internal static Mesh MeshFor(Sprite sp)
        {
            if (meshes.TryGetValue(sp, out var cached) && cached != null) return cached;
            if (failed.Contains(sp)) return null;
            var tex = sp.texture;
            Rect tr;
            try { tr = sp.textureRect; }
            catch { return PackedMeshFor(sp); }   // tightly packed atlas sprites expose their mesh, not a texture rectangle
            int w = Mathf.RoundToInt(tr.width), h = Mathf.RoundToInt(tr.height);
            if (w <= 0 || h <= 0) { failed.Add(sp); return null; }
            var alpha = ReadAlpha(tex, tr, w, h);

            float ppu = sp.pixelsPerUnit;
            // Pixel (0,0) of the texture rectangle in sprite-local units.
            Vector2 origin = (sp.textureRectOffset - sp.pivot) / ppu;
            float tw = tex.width, th = tex.height;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var cols = new List<Color>();
            var tris = new List<int>();

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, float shade)
            {
                int i = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                uvs.Add(ua); uvs.Add(ub); uvs.Add(uc); uvs.Add(ud);
                var col = new Color(shade, shade, shade, 1f);
                cols.Add(col); cols.Add(col); cols.Add(col); cols.Add(col);
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2); tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
            }

            Vector2 Uv(float px, float py) => new Vector2((tr.x + px) / tw, (tr.y + py) / th);
            Vector3 P(float px, float py, float z) => new Vector3(origin.x + px / ppu, origin.y + py / ppu, z);
            bool Solid(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && alpha[y * w + x] > 0.5f;

            // Back face (seen from behind, darker), then the walls, then the front face.
            Quad(P(0, 0, 0.5f), P(w, 0, 0.5f), P(w, h, 0.5f), P(0, h, 0.5f), Uv(0, 0), Uv(w, 0), Uv(w, h), Uv(0, h), 0.6f);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!Solid(x, y)) continue;
                    var c = Uv(x + 0.5f, y + 0.5f);   // the wall takes the colour of its edge pixel
                    if (!Solid(x - 1, y)) Quad(P(x, y, -0.5f), P(x, y + 1, -0.5f), P(x, y + 1, 0.5f), P(x, y, 0.5f), c, c, c, c, 0.75f);
                    if (!Solid(x + 1, y)) Quad(P(x + 1, y, -0.5f), P(x + 1, y, 0.5f), P(x + 1, y + 1, 0.5f), P(x + 1, y + 1, -0.5f), c, c, c, c, 0.75f);
                    if (!Solid(x, y + 1)) Quad(P(x, y + 1, -0.5f), P(x + 1, y + 1, -0.5f), P(x + 1, y + 1, 0.5f), P(x, y + 1, 0.5f), c, c, c, c, 0.95f);
                    if (!Solid(x, y - 1)) Quad(P(x, y, -0.5f), P(x, y, 0.5f), P(x + 1, y, 0.5f), P(x + 1, y, -0.5f), c, c, c, c, 0.55f);
                }
            Quad(P(0, 0, -0.5f), P(w, 0, -0.5f), P(w, h, -0.5f), P(0, h, -0.5f), Uv(0, 0), Uv(w, 0), Uv(w, h), Uv(0, h), 1f);

            var mesh = new Mesh { name = "CasualtyVR.Extruded " + sp.name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            meshes[sp] = mesh;
            return mesh;
        }

        private static Mesh PackedMeshFor(Sprite sprite)
        {
            if (meshes.TryGetValue(sprite, out var cached) && cached != null) return cached;
            if (failed.Contains(sprite)) return null;
            var sourceVertices = sprite.vertices;
            var sourceUvs = sprite.uv;
            var sourceTriangles = sprite.triangles;
            if (sourceVertices == null || sourceUvs == null || sourceTriangles == null
                || sourceVertices.Length < 3 || sourceUvs.Length != sourceVertices.Length
                || sourceTriangles.Length < 3)
            {
                failed.Add(sprite);
                return null;
            }

            var vertices = new List<Vector3>(sourceTriangles.Length * 2 + sourceVertices.Length * 8);
            var uvs = new List<Vector2>(vertices.Capacity);
            var colors = new List<Color>(vertices.Capacity);
            var triangles = new List<int>(sourceTriangles.Length * 2 + sourceVertices.Length * 12);

            for (int i = 0; i < sourceTriangles.Length; i += 3)
            {
                int first = vertices.Count;
                for (int j = 0; j < 3; j++)
                {
                    int index = sourceTriangles[i + j];
                    vertices.Add(new Vector3(sourceVertices[index].x, sourceVertices[index].y, -0.5f));
                    uvs.Add(sourceUvs[index]);
                    colors.Add(Color.white);
                }
                triangles.Add(first);
                triangles.Add(first + 1);
                triangles.Add(first + 2);

                int back = vertices.Count;
                for (int j = 0; j < 3; j++)
                {
                    int index = sourceTriangles[i + j];
                    vertices.Add(new Vector3(sourceVertices[index].x, sourceVertices[index].y, 0.5f));
                    uvs.Add(sourceUvs[index]);
                    colors.Add(new Color(0.6f, 0.6f, 0.6f, 1f));
                }
                triangles.Add(back);
                triangles.Add(back + 2);
                triangles.Add(back + 1);
            }

            var edgeCounts = new Dictionary<ulong, (int a, int b, int count)>();
            for (int i = 0; i < sourceTriangles.Length; i += 3)
            {
                AddEdge(sourceTriangles[i], sourceTriangles[i + 1]);
                AddEdge(sourceTriangles[i + 1], sourceTriangles[i + 2]);
                AddEdge(sourceTriangles[i + 2], sourceTriangles[i]);
            }

            foreach (var edge in edgeCounts.Values)
            {
                if (edge.count != 1) continue;
                int first = vertices.Count;
                var a = sourceVertices[edge.a];
                var b = sourceVertices[edge.b];
                vertices.Add(new Vector3(a.x, a.y, -0.5f));
                vertices.Add(new Vector3(b.x, b.y, -0.5f));
                vertices.Add(new Vector3(b.x, b.y, 0.5f));
                vertices.Add(new Vector3(a.x, a.y, 0.5f));
                uvs.Add(sourceUvs[edge.a]);
                uvs.Add(sourceUvs[edge.b]);
                uvs.Add(sourceUvs[edge.b]);
                uvs.Add(sourceUvs[edge.a]);
                var shade = new Color(0.75f, 0.75f, 0.75f, 1f);
                colors.Add(shade); colors.Add(shade); colors.Add(shade); colors.Add(shade);
                triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
                triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3);
            }

            var mesh = new Mesh { name = "CasualtyVR.Extruded " + sprite.name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            meshes[sprite] = mesh;
            return mesh;

            void AddEdge(int a, int b)
            {
                int low = Mathf.Min(a, b), high = Mathf.Max(a, b);
                ulong key = ((ulong)(uint)low << 32) | (uint)high;
                if (edgeCounts.TryGetValue(key, out var edge))
                    edgeCounts[key] = (edge.a, edge.b, edge.count + 1);
                else edgeCounts.Add(key, (a, b, 1));
            }
        }

        private static Mesh BoxMeshFor(Sprite sprite)
        {
            if (boxMeshes.TryGetValue(sprite, out var cached) && cached != null) return cached;

            Rect rect;
            try { rect = sprite.textureRect; }
            catch { failed.Add(sprite); return null; }
            Texture texture = sprite.texture;
            float tw = texture.width, th = texture.height;
            Vector2 uv00 = new Vector2(rect.xMin / tw, rect.yMin / th);
            Vector2 uv10 = new Vector2(rect.xMax / tw, rect.yMin / th);
            Vector2 uv11 = new Vector2(rect.xMax / tw, rect.yMax / th);
            Vector2 uv01 = new Vector2(rect.xMin / tw, rect.yMax / th);
            var vertices = new List<Vector3>(24);
            var uvs = new List<Vector2>(24);
            var triangles = new List<int>(36);

            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int first = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
                uvs.Add(uv00); uvs.Add(uv10); uvs.Add(uv11); uvs.Add(uv01);
                triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 1);
                triangles.Add(first); triangles.Add(first + 3); triangles.Add(first + 2);
            }

            const float half = 0.5f;
            Face(new Vector3(-half, -half, -half), new Vector3(half, -half, -half),
                new Vector3(half, half, -half), new Vector3(-half, half, -half));
            Face(new Vector3(half, -half, half), new Vector3(-half, -half, half),
                new Vector3(-half, half, half), new Vector3(half, half, half));
            Face(new Vector3(-half, half, -half), new Vector3(half, half, -half),
                new Vector3(half, half, half), new Vector3(-half, half, half));
            Face(new Vector3(-half, -half, half), new Vector3(half, -half, half),
                new Vector3(half, -half, -half), new Vector3(-half, -half, -half));
            Face(new Vector3(-half, -half, half), new Vector3(-half, -half, -half),
                new Vector3(-half, half, -half), new Vector3(-half, half, half));
            Face(new Vector3(half, -half, -half), new Vector3(half, -half, half),
                new Vector3(half, half, half), new Vector3(half, half, -half));

            var mesh = new Mesh { name = "CasualtyVR.Box " + sprite.name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            boxMeshes[sprite] = mesh;
            return mesh;
        }

        /// <summary>The sprite's alpha via the GPU (game textures are not CPU-readable).</summary>
        private static float[] ReadAlpha(Texture tex, Rect r, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var read = new Texture2D(w, h, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0);
            read.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var px = read.GetPixels32();
            Object.Destroy(read);
            var a = new float[w * h];
            for (int i = 0; i < a.Length; i++) a[i] = px[i].a / 255f;
            return a;
        }

        // ---- eye-only swap ------------------------------------------------------------------------------

        private void OnBegin(ScriptableRenderContext ctx, Camera cam)
        {
            bool desktopFirstPerson = DesktopFirstPerson.IsRenderCamera(cam);
            bool vrEye = VrRig.Instance != null && VrRig.Instance.IsEyeCamera(cam);
            if (!On || (!desktopFirstPerson && !vrEye) || (cam.cullingMask & (1 << VoxelTerrain.Layer)) == 0) return;
            foreach (var kv in entries)
            {
                var src = kv.Key;
                if (src == null || src.forceRenderingOff || kv.Value.Built == null || !kv.Value.Go.activeSelf) continue;
                src.forceRenderingOff = true;
                hidden.Add(src);
            }
        }

        private void OnEnd(ScriptableRenderContext ctx, Camera cam)
        {
            foreach (var s in hidden) if (s != null) s.forceRenderingOff = false;
            hidden.Clear();
        }

        private void Remove(SpriteRenderer src)
        {
            if (entries.TryGetValue(src, out var e))
            {
                if (e.DesktopHidden && src != null) src.forceRenderingOff = e.OriginalForceOff;
                if (e.Go != null) Object.Destroy(e.Go);
                if (e.DepthGo != null) Object.Destroy(e.DepthGo);
            }
            entries.Remove(src);
            Extruded.Remove(src);
        }

        private void ClearAll()
        {
            OnEnd(default, null);
            foreach (var e in entries.Values)
            {
                if (e.DesktopHidden && e.Source != null) e.Source.forceRenderingOff = e.OriginalForceOff;
                if (e.Go != null) Object.Destroy(e.Go);
                if (e.DepthGo != null) Object.Destroy(e.DepthGo);
            }
            entries.Clear();
            Extruded.Clear();
        }

        public void Destroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBegin;
            RenderPipelineManager.endCameraRendering -= OnEnd;
            ClearAll();
            foreach (var m in depthMaterials.Values) if (m != null) Object.Destroy(m);
            depthMaterials.Clear();
            if (opaqueBoxDepthMaterial != null) Object.Destroy(opaqueBoxDepthMaterial);
            opaqueBoxDepthMaterial = null;
            Object.Destroy(root);
        }
    }
}
