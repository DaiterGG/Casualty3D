using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Tilemaps;

namespace CasualtyVR
{
    /// <summary>
    /// Phase 4: the terrain as real 3D blocks for the headset (the DramaticShapeVoxelMod idea: extrude the
    /// 2D terrain into geometry). One mesh per game chunk, built from each tile's central block-sized sprite
    /// crop, per-cell transforms (the game randomly rotates/flips tiles) and tints. Oversized sprite overhangs
    /// are discarded so transparent art outside the solid tile core cannot create dark depth artifacts.
    /// Side faces exist only next to empty cells and are streaked from the tile's edge texels.
    ///
    /// Only the eye cameras see it (layer 25). While an eye renders, the 2D chunk TilemapRenderers are hidden
    /// with forceRenderingOff, which leaves `enabled` alone: the game uses `enabled` for chunk logic
    /// (ChunkScript.Update, UpdateChunkVisibility). Colliders and physics are untouched.
    ///
    /// Depth: 2D sprites never write depth but do depth-test (LEqual). So each chunk draws twice, first in
    /// the Default sorting layer: a depth-only prepass (URP Particles/Unlit, Blend Zero One, ZWrite On,
    /// alpha clip), then the lit tile art (the game's Sprite-Lit material, so 2D lights still apply). Every
    /// sprite drawn afterwards is hidden where it lies behind a block face and visible in the tunnels.
    /// </summary>
    internal sealed class VoxelTerrain
    {
        public const int Layer = 25;   // VR-only, unused by the game (probe: layers 0-10)

        private sealed class ChunkMesh
        {
            public GameObject Depth, Color, Clear;
            public Mesh Mesh, ClearMesh;
            public MeshRenderer DepthRenderer, ColorRenderer, ClearRenderer;
            public bool Final;           // built after the game's tile randomisation finished
            public float RandomizedAt = -1f;
        }

        private static readonly AccessTools.FieldRef<ChunkScript, bool> HasRandomized =
            AccessTools.FieldRefAccess<ChunkScript, bool>("hasRandomized");

        private readonly GameObject root;
        private readonly Dictionary<Vector2Int, ChunkMesh> meshes = new Dictionary<Vector2Int, ChunkMesh>();
        private readonly HashSet<Vector2Int> dirty = new HashSet<Vector2Int>();
        private readonly Dictionary<Texture, Material> colorMats = new Dictionary<Texture, Material>();
        private readonly Dictionary<Texture, Material> depthMats = new Dictionary<Texture, Material>();
        private readonly List<TilemapRenderer> hidden = new List<TilemapRenderer>();
        private readonly Shader depthShader;
        private WorldGeneration hooked;
        private bool filled;
        private float builtWithScale = -1f;

        // Scratch geometry reused across rebuilds: solid blocks, and see-through blocks (glass, leaves,
        // scaffolding), which are drawn without the depth prepass so what is behind them shows.
        private sealed class Buffers
        {
            public readonly List<Vector3> Verts = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<Color> Colors = new List<Color>();
            public readonly Dictionary<Texture, List<int>> TrisByTex = new Dictionary<Texture, List<int>>();

            public void Clear()
            {
                Verts.Clear(); Uvs.Clear(); Colors.Clear();
                foreach (var l in TrisByTex.Values) l.Clear();
            }

            public List<int> For(Texture tex)
            {
                if (!TrisByTex.TryGetValue(tex, out var tris)) TrisByTex[tex] = tris = new List<int>();
                return tris;
            }
        }
        private readonly Buffers solid = new Buffers(), clear = new Buffers();
        private Buffers cur;
        private readonly HashSet<int> seeThrough = new HashSet<int>();

        public bool Visible => Plugin.VoxelsOn.Value && PopupDepth.Scale > 0f;
        public float LastBuildMs;
        public int ChunkCount => meshes.Count;
        public int PendingCount => dirty.Count;

        public VoxelTerrain()
        {
            root = new GameObject("CasualtyVR.Voxels") { hideFlags = HideFlags.DontSave };
            Object.DontDestroyOnLoad(root);
            foreach (var id in Plugin.SeeThroughBlocks.Value.Split(','))
                if (int.TryParse(id.Trim(), out int n)) seeThrough.Add(n);
            depthShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (depthShader == null) Plugin.Log.LogWarning("Voxels: URP Particles/Unlit not found; blocks will not occlude sprites");
            RenderPipelineManager.beginCameraRendering += OnBegin;
            RenderPipelineManager.endCameraRendering += OnEnd;
        }

        /// <summary>Front face z (negative = towards the viewer) and back z, in world units.</summary>
        /// First person: a corridor centred on the play plane (FirstPerson.CorridorWidth), so actors on the play
        /// plane are straight ahead of the player's eye.
        public static float FrontZ => -(FirstPerson.Active ? FirstPerson.HalfWidth : Plugin.VoxelFront.Value) * PopupDepth.Scale;
        public static float BackZ => (FirstPerson.Active ? FirstPerson.HalfWidth : Plugin.VoxelBack.Value) * PopupDepth.Scale;

        public void Update()
        {
            var world = WorldGeneration.world;
            if (world == null || world.renderChunks == null || world.ChunkUpdated == null || world.generatingWorld)
            {
                if (hooked != null) Clear();
                return;
            }
            if (world != hooked) Hook(world);
            // Rebuild everything when the depths change (VR settings menu).
            float buildKey = PopupDepth.Scale * 1000f + FrontZ * 10f + BackZ * 0.1f;
            if (!Mathf.Approximately(builtWithScale, buildKey))
            {
                builtWithScale = buildKey;
                foreach (var k in meshes.Keys) dirty.Add(k);
            }

            if (Plugin.GreenscreenOn != filled)
            {
                filled = Plugin.GreenscreenOn;
                foreach (var kv in depthMats) if (kv.Value != null) ConfigureDepth(kv.Value, kv.Key);
            }

            bool visible = Visible;
            if (root.activeSelf != visible) root.SetActive(visible);
            if (!visible) return;

            int cw = world.renderChunks.GetLength(0), ch = world.renderChunks.GetLength(1);
            float now = Time.unscaledTime;
            for (int x = 0; x < cw; x++)
                for (int y = 0; y < ch; y++)
                {
                    var tr = world.renderChunks[x, y];
                    bool on = tr != null && tr.enabled;
                    var key = new Vector2Int(x, y);
                    meshes.TryGetValue(key, out var cm);
                    if (on && cm == null) dirty.Add(key);
                    if (on && cm != null && !cm.Final)
                    {
                        // ChunkScript randomises the tile rotations over ~64 frames after its first render;
                        // rebuild once that has finished so the front faces match.
                        var script = tr.GetComponent<ChunkScript>();
                        if (script != null && HasRandomized(script))
                        {
                            if (cm.RandomizedAt < 0f) cm.RandomizedAt = now;
                            else if (now - cm.RandomizedAt > 1.5f) { cm.Final = true; dirty.Add(key); }
                        }
                    }
                    if (cm != null)
                    {
                        if (cm.Depth.activeSelf != on) cm.Depth.SetActive(on);
                        if (cm.Color.activeSelf != on) cm.Color.SetActive(on);
                        if (cm.Clear.activeSelf != on) cm.Clear.SetActive(on);
                    }
                }

            if (dirty.Count == 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int built = 0;
            Vector2 focus = Camera.main != null ? (Vector2)Camera.main.transform.position : Vector2.zero;
            while (dirty.Count > 0 && (built == 0 || sw.Elapsed.TotalMilliseconds < Plugin.VoxelBudgetMs.Value))
            {
                // Nearest pending chunk first: the one around the player matters most.
                Vector2Int next = default;
                float best = float.MaxValue;
                foreach (var d in dirty)
                {
                    var tr = world.renderChunks[d.x, d.y];
                    float dist = tr != null ? ((Vector2)tr.transform.position - focus).sqrMagnitude : float.MaxValue;
                    if (dist < best) { best = dist; next = d; }
                }
                dirty.Remove(next);
                var ctr = world.renderChunks[next.x, next.y];
                if (ctr == null || !ctr.enabled) continue;
                Build(world, next, ctr);
                built++;
            }
            LastBuildMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        private void Hook(WorldGeneration world)
        {
            Clear();
            hooked = world;
            int cw = world.ChunkUpdated.GetLength(0), ch = world.ChunkUpdated.GetLength(1);
            for (int x = 0; x < cw; x++)
                for (int y = 0; y < ch; y++)
                {
                    var key = new Vector2Int(x, y);
                    world.ChunkUpdated[x, y]?.AddListener(() => MarkDirtyAround(key));
                }
            Plugin.Log.LogInfo($"Voxels: hooked world {world.width}x{world.height}, {cw}x{ch} chunks");
        }

        // Edge cells' side faces depend on the neighbouring chunk, so rebuild built neighbours too.
        private void MarkDirtyAround(Vector2Int c)
        {
            if (hooked == null) return;
            foreach (var n in new[] { c, c + Vector2Int.left, c + Vector2Int.right, c + Vector2Int.up, c + Vector2Int.down })
                if (meshes.ContainsKey(n)) dirty.Add(n);
        }

        private void Build(WorldGeneration world, Vector2Int c, TilemapRenderer tr)
        {
            var map = tr.GetComponent<Tilemap>();
            int size = WorldGeneration.CHUNKSIZE, half = world.HALFCHUNKSIZE;
            float zf = FrontZ, zb = BackZ;

            solid.Clear();
            clear.Clear();

            // Sides (and see-through back panes) first, then fronts: painter's order within each mesh.
            for (int pass = 0; pass < 2; pass++)
                for (int lx = 0; lx < size; lx++)
                    for (int ly = 0; ly < size; ly++)
                    {
                        int bx = c.x * size + lx, by = c.y * size + ly;
                        int id = world.GetBlock(new Vector2Int(bx, by));
                        if (id == 0) continue;
                        bool glassy = seeThrough.Contains(id);
                        bool up = false, down = false, left = false, right = false;
                        if (pass == 0)
                        {
                            // A solid block shows a side towards air and towards see-through blocks; a see-through
                            // block only towards air (glass next to glass is one pane).
                            up = Open(world, bx, by + 1, glassy);
                            down = Open(world, bx, by - 1, glassy);
                            left = Open(world, bx - 1, by, glassy);
                            right = Open(world, bx + 1, by, glassy);
                            if (!(up || down || left || right || glassy)) continue;
                        }

                        var cell = new Vector3Int(lx - half, ly - half, 0);
                        Sprite sp = map.GetSprite(cell);
                        if (sp == null) continue;
                        Texture tex = sp.texture;
                        cur = glassy ? clear : solid;
                        var tris = cur.For(tex);
                        Matrix4x4 inv = map.GetTransformMatrix(cell).inverse;
                        Color tint = map.GetColor(cell);
                        float x0 = lx - half, y0 = ly - half, x1 = x0 + 1f, y1 = y0 + 1f;
                        Vector2 uv00 = Uv(inv, sp, -0.5f, -0.5f), uv10 = Uv(inv, sp, 0.5f, -0.5f),
                            uv11 = Uv(inv, sp, 0.5f, 0.5f), uv01 = Uv(inv, sp, -0.5f, 0.5f);

                        if (pass == 1)
                        {
                            Quad(tris, new Vector3(x0, y0, zf), new Vector3(x1, y0, zf), new Vector3(x1, y1, zf), new Vector3(x0, y1, zf),
                                 uv00, uv10, uv11, uv01, tint);
                            continue;
                        }
                        if (glassy)   // back pane
                            Quad(tris, new Vector3(x0, y0, zb), new Vector3(x1, y0, zb), new Vector3(x1, y1, zb), new Vector3(x0, y1, zb),
                                 uv00, uv10, uv11, uv01, Shade(tint, 0.8f));
                        if (up) Side(tris, 0, new Vector2(x0, y1), new Vector2(x1, y1), zf, zb, inv, sp, Shade(tint, 0.95f));
                        if (down) Side(tris, 1, new Vector2(x0, y0), new Vector2(x1, y0), zf, zb, inv, sp, Shade(tint, 0.55f));
                        if (left) Side(tris, 2, new Vector2(x0, y0), new Vector2(x0, y1), zf, zb, inv, sp, Shade(tint, 0.75f));
                        if (right) Side(tris, 3, new Vector2(x1, y0), new Vector2(x1, y1), zf, zb, inv, sp, Shade(tint, 0.75f));
                    }

            AddTopBoundary(world, c, size, half, zf, zb);

            if (!meshes.TryGetValue(c, out var cm))
            {
                string name = $"CasualtyVR.VoxelChunk {c.x},{c.y}";
                cm = new ChunkMesh { Mesh = NewMesh(name), ClearMesh = NewMesh(name + " clear") };
                cm.Depth = MakeRenderer(name + " depth", cm.Mesh, tr, short.MinValue, out cm.DepthRenderer);
                cm.Color = MakeRenderer(name, cm.Mesh, tr, short.MinValue + 1, out cm.ColorRenderer);
                // See-through blocks draw where the flat chunk did (after the background and the actors),
                // depth-tested but not depth-writing.
                cm.Clear = MakeRenderer(name + " clear", cm.ClearMesh, tr, tr.sortingOrder, out cm.ClearRenderer);
                meshes[c] = cm;
            }
            foreach (var go in new[] { cm.Depth, cm.Color, cm.Clear }) go.transform.position = tr.transform.position;

            var colorList = Upload(cm.Mesh, solid, world);
            var depthList = new Material[colorList.Length];
            for (int i = 0; i < colorList.Length; i++) depthList[i] = DepthMaterial(colorList[i].mainTexture);
            cm.ColorRenderer.sharedMaterials = colorList;
            cm.DepthRenderer.sharedMaterials = depthList;
            cm.DepthRenderer.enabled = depthShader != null && colorList.Length > 0;
            cm.ColorRenderer.enabled = colorList.Length > 0;
            var clearList = Upload(cm.ClearMesh, clear, world);
            cm.ClearRenderer.sharedMaterials = clearList;
            cm.ClearRenderer.enabled = clearList.Length > 0;
        }

        private void AddTopBoundary(WorldGeneration world, Vector2Int chunk, int size, int half, float zf, float zb)
        {
            if (chunk.y != world.renderChunks.GetLength(1) - 1 || world.tiles == null || world.tiles.Length <= 1
                || world.tileColors == null || world.tileColors.Length <= 1) return;
            Sprite stone = (world.tiles[1] as Tile)?.sprite;
            if (stone == null) return;

            int height = (int)world.height;
            int localY = height - chunk.y * size;
            float y0 = localY - half, y1 = y0 + 1f;
            Texture tex = stone.texture;
            Matrix4x4 transform = Matrix4x4.identity;
            Color tint = world.tileColors[1];
            Vector2 uv00 = Uv(transform, stone, -0.5f, -0.5f), uv10 = Uv(transform, stone, 0.5f, -0.5f),
                uv11 = Uv(transform, stone, 0.5f, 0.5f), uv01 = Uv(transform, stone, -0.5f, 0.5f);
            cur = solid;
            var tris = solid.For(tex);
            for (int lx = 0; lx < size; lx++)
            {
                int bx = chunk.x * size + lx;
                if (bx >= world.width) continue;
                float x0 = lx - half, x1 = x0 + 1f;
                Quad(tris, new Vector3(x0, y0, zf), new Vector3(x1, y0, zf), new Vector3(x1, y1, zf), new Vector3(x0, y1, zf),
                    uv00, uv10, uv11, uv01, tint);
                Side(tris, 0, new Vector2(x0, y1), new Vector2(x1, y1), zf, zb, transform, stone, Shade(tint, 0.95f));
                if (Open(world, bx, height - 1, false))
                    Side(tris, 1, new Vector2(x0, y0), new Vector2(x1, y0), zf, zb, transform, stone, Shade(tint, 0.55f));
                if (lx == 0)
                    Side(tris, 2, new Vector2(x0, y0), new Vector2(x0, y1), zf, zb, transform, stone, Shade(tint, 0.75f));
                if (bx == (int)world.width - 1)
                    Side(tris, 3, new Vector2(x1, y0), new Vector2(x1, y1), zf, zb, transform, stone, Shade(tint, 0.75f));
            }
        }

        private static Mesh NewMesh(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();
            return mesh;
        }

        private Material[] Upload(Mesh m, Buffers b, WorldGeneration world)
        {
            m.Clear();
            m.SetVertices(b.Verts);
            m.SetUVs(0, b.Uvs);
            m.SetColors(b.Colors);
            int sub = 0;
            foreach (var kv in b.TrisByTex) if (kv.Value.Count > 0) sub++;
            m.subMeshCount = Mathf.Max(sub, 1);
            var mats = new Material[sub];
            sub = 0;
            foreach (var kv in b.TrisByTex)
            {
                if (kv.Value.Count == 0) continue;
                mats[sub] = ColorMaterial(kv.Key, world);
                m.SetTriangles(kv.Value, sub++, false);
            }
            m.RecalculateBounds();
            return mats;
        }

        private bool Open(WorldGeneration world, int bx, int by, bool fromSeeThrough)
        {
            int id = world.GetBlock(new Vector2Int(bx, by));
            return id == 0 || (!fromSeeThrough && seeThrough.Contains(id));
        }

        private GameObject MakeRenderer(string name, Mesh mesh, TilemapRenderer like, int order, out MeshRenderer mr)
        {
            var go = new GameObject(name) { layer = Layer, hideFlags = HideFlags.DontSave };
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.sortingLayerID = like.sortingLayerID;   // Default: the layer the game's lights target
            mr.sortingOrder = order;                   // before every sprite of the layer
            return go;
        }

        // Texture UV for point (px,py) in tile space (-0.5..0.5), through the inverse of the tile's rotation/flip.
        private static Vector2 Uv(Matrix4x4 inv, Sprite sprite, float px, float py)
        {
            Vector3 q = inv.MultiplyPoint3x4(new Vector3(px, py, 0f));
            Rect rect = sprite.textureRect;
            Vector2 size = sprite.bounds.size;
            // Terrain sprites may overhang their tile; sample only the centered one-world-unit tile region.
            float sourceWidth = Mathf.Max(1f, size.x), sourceHeight = Mathf.Max(1f, size.y);
            float u = Mathf.Clamp(0.5f + q.x / sourceWidth, 0.002f, 0.998f);
            float v = Mathf.Clamp(0.5f + q.y / sourceHeight, 0.002f, 0.998f);
            return new Vector2((rect.x + u * rect.width) / sprite.texture.width,
                (rect.y + v * rect.height) / sprite.texture.height);
        }

        /// <summary>
        /// A side face (0 top, 1 bottom, 2 left, 3 right) from a..b (the block edge, in tile-coordinate order)
        /// back to zb, one segment per block of depth. Each segment shows the whole tile, folded at the
        /// segment boundaries (front edge row -> opposite row -> front edge row ...), so the side continues
        /// the front face seamlessly instead of streaking one texel row (headset feedback 2026-10-04).
        /// </summary>
        private void Side(List<int> tris, int side, Vector2 a, Vector2 b, float zf, float zb, Matrix4x4 inv, Sprite sprite, Color col)
        {
            float depth = zb - zf;
            for (float d0 = 0f; d0 < depth - 1e-3f; d0 += 1f)
            {
                float d1 = Mathf.Min(d0 + 1f, depth);
                // d0 is a whole block: even segments run front row -> opposite row, odd ones back again.
                float f0 = Fold(d0), f1 = f0 + (Mathf.RoundToInt(d0) % 2 == 0 ? 1f : -1f) * (d1 - d0);
                Quad(tris, new Vector3(a.x, a.y, zf + d0), new Vector3(b.x, b.y, zf + d0), new Vector3(b.x, b.y, zf + d1), new Vector3(a.x, a.y, zf + d1),
                     SideUv(inv, sprite, side, -0.5f, f0), SideUv(inv, sprite, side, 0.5f, f0),
                     SideUv(inv, sprite, side, 0.5f, f1), SideUv(inv, sprite, side, -0.5f, f1), col);
            }
        }

        // Triangle wave 0..1..0 with period 2: how far into the tile (from the front edge) depth d samples.
        private static float Fold(float d)
        {
            float f = d % 2f;
            return f <= 1f ? f : 2f - f;
        }

        // s runs along the block edge (-0.5..0.5), f from the front edge row (0) to the opposite row (1).
        private static Vector2 SideUv(Matrix4x4 inv, Sprite sprite, int side, float s, float f)
        {
            switch (side)
            {
                case 0: return Uv(inv, sprite, s, 0.5f - f);
                case 1: return Uv(inv, sprite, s, -0.5f + f);
                case 2: return Uv(inv, sprite, -0.5f + f, s);
                default: return Uv(inv, sprite, 0.5f - f, s);
            }
        }

        private static Color Shade(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

        private void Quad(List<int> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Color col)
        {
            var verts = cur.Verts;
            int i = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            cur.Uvs.Add(ua); cur.Uvs.Add(ub); cur.Uvs.Add(uc); cur.Uvs.Add(ud);
            cur.Colors.Add(col); cur.Colors.Add(col); cur.Colors.Add(col); cur.Colors.Add(col);
            // Both shaders draw with culling off, so one winding is enough.
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2); tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }

        private Material ColorMaterial(Texture tex, WorldGeneration world)
        {
            if (colorMats.TryGetValue(tex, out var m) && m != null) return m;
            // The game's own tile material (Sprite-Lit-Default): 2D lights apply exactly as on the flat tiles.
            m = new Material(world.defaultMat) { name = "CasualtyVR.Voxel " + tex.name, mainTexture = tex };
            colorMats[tex] = m;
            return m;
        }

        private Material DepthMaterial(Texture tex)
        {
            if (depthMats.TryGetValue(tex, out var m) && m != null) return m;
            m = new Material(depthShader) { name = "CasualtyVR.VoxelDepth " + tex.name };
            ConfigureDepth(m, tex);
            depthMats[tex] = m;
            return m;
        }

        /// <summary>
        /// Normally depth only (Blend Zero One, alpha clip): the tile art's transparent texels show the game's
        /// backdrop as in 2D. With the passthrough greenscreen the backdrop is hidden, so the prepass instead
        /// fills each block opaque in a dark rock colour (no clip): blocks stay solid instead of showing the
        /// key colour through their holes.
        /// </summary>
        private void ConfigureDepth(Material m, Texture tex)
        {
            bool fill = Plugin.GreenscreenOn;
            m.SetTexture("_BaseMap", fill ? Texture2D.whiteTexture : tex);
            m.SetColor("_BaseColor", fill ? new Color(0.05f, 0.05f, 0.06f, 1f) : Color.white);
            m.SetFloat("_Surface", 0f);
            m.SetFloat("_SrcBlend", (float)(fill ? BlendMode.One : BlendMode.Zero));
            m.SetFloat("_DstBlend", (float)(fill ? BlendMode.Zero : BlendMode.One));
            m.SetFloat("_SrcBlendAlpha", (float)(fill ? BlendMode.One : BlendMode.Zero));
            m.SetFloat("_DstBlendAlpha", (float)(fill ? BlendMode.Zero : BlendMode.One));
            m.SetFloat("_ZWrite", 1f);
            m.SetFloat("_Cull", 0f);
            m.SetFloat("_AlphaClip", fill ? 0f : 1f);
            m.SetFloat("_Cutoff", 0.5f);
            if (fill) m.DisableKeyword("_ALPHATEST_ON"); else m.EnableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)RenderQueue.AlphaTest;
        }

        private static bool IsEye(Camera cam) => VrRig.Instance != null && VrRig.Instance.IsEyeCamera(cam);

        private void OnBegin(ScriptableRenderContext ctx, Camera cam)
        {
            if (!IsEye(cam) || (cam.cullingMask & (1 << Layer)) == 0) return;
            BeginEye();
        }

        private void OnEnd(ScriptableRenderContext ctx, Camera cam)
        {
            if (hidden.Count > 0) EndEye();
        }

        /// <summary>Hide the built chunks' 2D renderers for an eye camera's render (EndEye restores them).</summary>
        private void BeginEye()
        {
            hidden.Clear();
            var world = WorldGeneration.world;
            if (!Visible || world == null || world != hooked || world.renderChunks == null) return;
            int cw = world.renderChunks.GetLength(0), ch = world.renderChunks.GetLength(1);
            for (int x = 0; x < cw; x++)
                for (int y = 0; y < ch; y++)
                {
                    var tr = world.renderChunks[x, y];
                    // Chunks without a mesh yet stay flat rather than vanish.
                    if (tr == null || !tr.enabled || tr.forceRenderingOff || !meshes.ContainsKey(new Vector2Int(x, y))) continue;
                    tr.forceRenderingOff = true;
                    hidden.Add(tr);
                }
        }

        private void EndEye()
        {
            foreach (var tr in hidden) if (tr != null) tr.forceRenderingOff = false;
            hidden.Clear();
        }

        public void Clear()
        {
            EndEye();
            foreach (var cm in meshes.Values)
            {
                Object.Destroy(cm.Mesh);
                Object.Destroy(cm.ClearMesh);
                Object.Destroy(cm.Depth);
                Object.Destroy(cm.Color);
                Object.Destroy(cm.Clear);
            }
            meshes.Clear();
            dirty.Clear();
            hooked = null;   // the old world's listeners go with it (a new run reloads the scene)
        }

        public void Destroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBegin;
            RenderPipelineManager.endCameraRendering -= OnEnd;
            Clear();
            foreach (var m in colorMats.Values) if (m != null) Object.Destroy(m);
            foreach (var m in depthMats.Values) if (m != null) Object.Destroy(m);
            colorMats.Clear();
            depthMats.Clear();
            Object.Destroy(root);
        }
    }
}
