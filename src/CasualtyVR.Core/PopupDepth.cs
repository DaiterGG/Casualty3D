using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CasualtyVR
{
    /// <summary>
    /// "Pop-up book" depth: while registered (VR) cameras render, renderers are moved along z according
    /// to their sorting layer/order, then restored once the last registered camera of the frame has
    /// rendered. Only z changes and Physics2D ignores z, so the game's logic camera and simulation never
    /// see it. 2D sorting still decides draw order (sprites do not depth-test), so z only adds stereo
    /// disparity. Depth bands come from the probe's draw-order census (docs/PHASE1_SPIKE_RECEIPT.md).
    /// +z is away from the viewer.
    ///
    /// Physics objects are never moved (see <see cref="SkipColliders"/>).
    ///
    /// Cost control: the renderer list is cached and refreshed every <see cref="RefreshFrames"/> frames,
    /// and the offsets are applied once per frame for all registered cameras (both eyes), not per camera.
    /// </summary>
    public static class PopupDepth
    {
        public static float Scale = 1f;
        public static int RefreshFrames = 120;
        public static float LastApplyMs;
        public static int LastCount;

        private struct Entry
        {
            public Transform Transform;
            public Renderer Renderer;
            public int HierarchyDepth;
            public Vector3 Original;
            public Quaternion OriginalRot;
            public float Depth;   // cached at refresh: DepthFor compares strings
            public Transform Owner;   // decoration of an extruded object in the tunnel: turns with it in first person
        }

        private static readonly List<Camera> cameras = new List<Camera>();
        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly HashSet<Transform> movedSet = new HashSet<Transform>();
        private static readonly List<Bounds> anchors = new List<Bounds>();
        private static int flattenedLogged;
        private static Dictionary<string, int> ownerLog = new Dictionary<string, int>();
        private static int refreshes;
        private static int cachedFrame = -1000;
        private static int appliedFrame = -1;
        private static int renderedThisFrame;
        private static bool applied;
        private static bool hooked;

        public static void Register(Camera cam)
        {
            if (!cameras.Contains(cam)) cameras.Add(cam);
            if (hooked) return;
            RenderPipelineManager.beginCameraRendering += OnBegin;
            RenderPipelineManager.endCameraRendering += OnEnd;
            hooked = true;
        }

        public static void Unregister(Camera cam)
        {
            if (applied) Restore();
            cameras.Remove(cam);
        }

        /// <summary>Recompute every renderer's band at the next eye render (first person on/off).</summary>
        public static void Invalidate() => cachedFrame = -1000;

        public static float DepthFor(string sortingLayer, int order)
        {
            if (FirstPerson.Active)
            {
                // First person: the camera is inside the slab. Background walls close its back side, backdrop
                // props stand against them, and everything else stays inside the corridor (FrontDepth..BackDepth),
                // never beyond it (the diorama's front bands would put speech, corpses and jump pads outside).
                float back = FirstPerson.HalfWidth, front = -FirstPerson.HalfWidth;
                if (sortingLayer == "Back") return back + 0.5f;
                if (sortingLayer == "Top" || sortingLayer == "LineOfSight") return 0f;
                if (order <= -8000) return back + 0.02f;
                if (order <= -100) return back - 0.1f;
                if (order < 1000) return Mathf.Clamp(-order * 0.002f, front + 0.1f, back - 0.1f);
                return 0f;
            }
            if (sortingLayer == "Back") return 12f;                // rock background
            if (sortingLayer == "Top" || sortingLayer == "LineOfSight")
            {
                if (order >= 32000) return -3f;                    // speech text
                if (order <= -100) return 0f;                      // liquids (Top/-500 water particles) and crystals sit in the world,
                                                                   // not in the fog band (user report: water looked weird)
                return -2f;                                        // fog, rain, hover squares
            }
            if (order <= -8000) return 4f;                         // background walls, wall holes, backdrop props
            if (order <= -100) return 1.5f;                        // background furniture, traders
            if (order < 1000) return -order * 0.002f;              // actor plane: higher draw order is nearer (front limbs
                                                                   // -0.34 .. back limbs +0.12; the sign was inverted until 2026-10-03)
            // Voxel blocks (Phase 4) stand out to FrontDepth; what is drawn over the terrain in 2D (block
            // cracks at 4000, corpses, jump pads, explosions) must sit just in front of their faces.
            float onTop = Plugin.VoxelsOn.Value ? -Plugin.VoxelFront.Value - 0.1f : 0f;
            if (order < 1100) return Mathf.Min(-0.6f, onTop);      // terrain blocks frame the tunnels
            return Mathf.Min(-0.8f, onTop);                        // corpses, jump pads, explosions, block cracks
        }

        private static void OnBegin(ScriptableRenderContext ctx, Camera cam)
        {
            if (!cameras.Contains(cam) || Scale == 0f) return;
            if (appliedFrame != Time.frameCount)
            {
                appliedFrame = Time.frameCount;
                renderedThisFrame = 0;
            }
            if (!applied) Apply();
        }

        private static void OnEnd(ScriptableRenderContext ctx, Camera cam)
        {
            if (!cameras.Contains(cam) || !applied) return;
            if (++renderedThisFrame >= CountActiveCameras()) Restore();
        }

        private static int CountActiveCameras()
        {
            int n = 0;
            foreach (var c in cameras) if (c && c.isActiveAndEnabled) n++;
            return n;
        }

        private static void Apply()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (Scans.Due(cachedFrame, RefreshFrames, 0)) { long t0 = Scans.Start(); RefreshCache(); Scans.Stop("pop-up depth", t0); }
            // Record every original first: once a parent moves, its children's world positions change.
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Transform) { e.Original = e.Transform.position; e.OriginalRot = e.Transform.rotation; }
                entries[i] = e;
            }
            foreach (var e in entries)
            {
                if (!e.Transform) continue;
                if (e.Owner != null && FirstPerson.Active)
                {
                    // On the turned object's front face (ObjectExtruder turns the object to face the player).
                    var o = e.Owner.position;
                    var pivot = new Vector3(o.x, o.y, 0f);
                    var r = FirstPerson.FaceYaw(pivot, lockDesktopDepth: true);
                    var local = new Vector3(e.Original.x - o.x, e.Original.y - o.y, e.Depth * Scale);
                    e.Transform.SetPositionAndRotation(pivot + r * local, r * e.OriginalRot);
                    continue;
                }
                e.Transform.position = new Vector3(e.Original.x, e.Original.y, e.Original.z + e.Depth * Scale);
            }
            PinMarkers();
            applied = true;
            LastApplyMs = (float)sw.Elapsed.TotalMilliseconds;
            LastCount = entries.Count;
        }

        private static void Restore()
        {
            // Same parent-first order: each child's recorded world position is valid once its parent is back.
            foreach (var e in entries)
                if (e.Transform)
                {
                    if (e.Owner != null) e.Transform.SetPositionAndRotation(e.Original, e.OriginalRot);
                    else e.Transform.position = e.Original;
                }
            for (int i = 0; i < pinned.Count; i++)
                if (pinned[i].r) { pinned[i].r.transform.SetPositionAndRotation(pinned[i].pos, pinned[i].rot); pinned[i].r.forceRenderingOff = pinned[i].off; }
            pinned.Clear();
            for (int i = 0; i < crackDrawModes.Count; i++)
                if (crackDrawModes[i].r)
                {
                    crackDrawModes[i].r.drawMode = crackDrawModes[i].mode;
                    crackDrawModes[i].r.size = crackDrawModes[i].size;
                }
            crackDrawModes.Clear();
            applied = false;
        }

        private static void RefreshCache()
        {
            cachedFrame = Time.frameCount;
            entries.Clear();
            anchors.Clear();
            int skipped = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                int layer = r.gameObject.layer;
                if (IsMarker(r)) continue;   // placed by PinMarkers
                // Sprites attached to a physics object (which never moves) stay with it: the drop capsule's
                // glowing emission panel floated 1.6 units in front of its capsule (user, 2026-10-04). On an
                // extruded object they sit on its front face.
                var owner = r.transform.parent != null ? r.transform.parent.GetComponentInParent<Collider2D>() : null;
                // Terrain chunks (tilemap/composite colliders) are not owners: the game parents its background
                // walls to them, and those keep their band depth.
                if (owner is UnityEngine.Tilemaps.TilemapCollider2D || owner is CompositeCollider2D || (owner != null && owner.GetComponent<UnityEngine.Tilemaps.Tilemap>() != null)) owner = null;
                if (owner != null && r.GetComponent<Collider2D>() == null)
                {
                    if (ownerLog != null) { ownerLog.TryGetValue(owner.name + " (" + owner.GetType().Name + ")", out int c); ownerLog[owner.name + " (" + owner.GetType().Name + ")"] = c + 1; }
                    var ownerSprite = owner.GetComponentInParent<SpriteRenderer>();
                    if (ObjectExtruder.On && ownerSprite != null && ObjectExtruder.Extruded.Contains(ownerSprite))
                    {
                        var ownerBuilding = owner.GetComponentInParent<BuildingEntity>();
                        int hd = 0;
                        for (var t = r.transform; t.parent != null; t = t.parent) hd++;
                        // Keep decorations on the same side of the tunnel as their extruded owner. Steel doors
                        // and crystals are foreground objects even if their source sprite uses a background order.
                        bool backWallObject = ownerSprite.sortingOrder <= -100
                            && !ObjectExtruder.IsSteelDoor(ownerBuilding, ownerSprite)
                            && !ObjectExtruder.IsCrystal(ownerBuilding);
                        float objectDepth = ObjectExtruder.DecorationDepthFor(ownerBuilding, ownerSprite);
                        float faceDepth = FirstPerson.Active && backWallObject
                            ? FirstPerson.HalfWidth - objectDepth - 0.04f
                            : -objectDepth * 0.5f - 0.02f;
                        entries.Add(new Entry { Transform = r.transform, Renderer = r, HierarchyDepth = hd, Depth = faceDepth,
                                                Owner = FirstPerson.Active && !backWallObject ? owner.transform : null });
                    }
                    continue;
                }
                if (layer == DioramaFrame.Layer || layer == VrUi.DesktopLayer || layer == VrUi.OverlayLayer || layer == VoxelTerrain.Layer) continue;   // our own objects place themselves
                if (SkipColliders && r.GetComponent<Collider2D>() != null)
                {
                    skipped++;
                    // Only background-band objects (buttons, pumps, heaters set into walls); corpses and the
                    // like in front bands have no decorations behind them.
                    if (r.enabled && DepthFor(r.sortingLayerName, r.sortingOrder) >= 1f) anchors.Add(r.bounds);
                    continue;
                }
                int depth = 0;
                for (var t = r.transform; t.parent != null; t = t.parent) depth++;
                float d = DepthFor(r.sortingLayerName, r.sortingOrder);
                // First person: a big see-through sprite on the play plane (the lifepod's glow circle) passes right
                // beside the eye and splits the view into a tinted and a clear half (user: "brightness seam").
                // It goes onto the back wall as a glow there.
                if (FirstPerson.Active && Mathf.Abs(d) < 0.5f && r is SpriteRenderer && !(r is UnityEngine.Tilemaps.TilemapRenderer))
                {
                    var bs = r.bounds.size;
                    if (bs.x * bs.y > 16f && !FirstPerson.OwnBody.Contains(r)) d = FirstPerson.HalfWidth - 0.2f;
                }
                entries.Add(new Entry { Transform = r.transform, Renderer = r, HierarchyDepth = depth, Depth = d });
            }
            // Decorations stay with the physics objects they belong to: a collider object cannot move, so
            // a small sprite drawn over or under one (the lifepod's glowing screen panel behind its buttons,
            // user report 2026-10-04 "illuminated section that is offset") keeps its depth (0) instead of
            // its band's. Tilemaps and big sprites (background walls) are left to their bands.
            if (anchors.Count > 0 && !FirstPerson.Active)   // first person: decorations go to the back wall with their objects
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e.Depth < 0.5f || e.Renderer is UnityEngine.Tilemaps.TilemapRenderer) continue;   // only what would be pushed behind
                    var b = e.Renderer.bounds;
                    if (b.size.x * b.size.y > 64f) continue;
                    foreach (var anchor in anchors)
                    {
                        var a2 = anchor; a2.Expand(new Vector3(0f, 0f, 1000f));   // overlap in x/y only
                        var b2 = b; b2.Expand(new Vector3(0f, 0f, 1000f));
                        if (a2.Intersects(b2))
                        {
                            e.Depth = 0f;
                            entries[i] = e;
                            if (Plugin.Verbose && flattenedLogged++ < 12) Plugin.Log.LogInfo($"Popup: '{e.Renderer.name}' ({e.Renderer.sortingOrder}) kept with its object at the actor plane");
                            break;
                        }
                    }
                }

            if (Plugin.Verbose && ownerLog != null && ++refreshes == 3)
            {
                foreach (var kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(ownerLog, k => k.Value), 10))
                    Plugin.Log.LogInfo($"Popup owner: {kv.Key} x{kv.Value}");
                ownerLog = null;
            }
                        // Parents before children, so each child's world z is set after its parent has moved.
            entries.Sort((a, b) => a.HierarchyDepth.CompareTo(b.HierarchyDepth));

            // Drop entries that would not move: no offset of their own and no moved ancestor (a moved
            // ancestor drags them along, so they must be corrected). ~2,300 renderers at order 0 qualify.
            movedSet.Clear();
            int kept = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                bool ancestorMoved = false;
                for (var t = e.Transform.parent; t != null && !ancestorMoved; t = t.parent) ancestorMoved = movedSet.Contains(t);
                if (Mathf.Abs(e.Depth) < 0.02f && !ancestorMoved) continue;
                if (Mathf.Abs(e.Depth) >= 0.02f) movedSet.Add(e.Transform);
                entries[kept++] = e;
            }
            entries.RemoveRange(kept, entries.Count - kept);
            LastSkipped = skipped;
        }

        // The game's hover highlight and block-placing ghost (PlayerCamera.hoverSquare / placeSquare) sit in
        // the Top layer, which the bands put 2 units in front of everything: in the headset the glow floated
        // in front of its object, offset and flickering against it (user report 2026-10-04). They are placed
        // on what they mark instead: a block's front face, or the actor plane for items and buildings.
        private static readonly List<(Renderer r, Vector3 pos, Quaternion rot, bool off)> pinned = new List<(Renderer, Vector3, Quaternion, bool)>();
        private static readonly List<(SpriteRenderer r, SpriteDrawMode mode, Vector2 size)> crackDrawModes =
            new List<(SpriteRenderer, SpriteDrawMode, Vector2)>();

        private static bool IsMarker(Renderer r)
        {
            var pc = PlayerCamera.main;
            return pc != null && (r == pc.hoverSquare || r == pc.placeSquare || IsCrack(r));
        }

        // Block cracks (BlockDamage.UpdateSprite: a root "blockbreak" sprite at order 4000).
        private static bool IsCrack(Renderer r) => r.sortingOrder == 4000 && r.name == "blockbreak" && r.transform.parent == null;
        private static readonly List<SpriteRenderer> cracks = new List<SpriteRenderer>();
        private static readonly HarmonyLib.AccessTools.FieldRef<BlockDamage, SpriteRenderer> CrackSprite =
            HarmonyLib.AccessTools.FieldRefAccess<BlockDamage, SpriteRenderer>("spr");

        private static float nextMarkerLog;

        private static void PinMarkers()
        {
            var pc = PlayerCamera.main;
            if (pc == null) return;
            bool voxels = Plugin.VoxelsOn.Value;
            foreach (var r in new Renderer[] { pc.hoverSquare, pc.placeSquare })
            {
                if (r == null) continue;
                var t = r.transform;
                pinned.Add((r, t.position, t.rotation, r.forceRenderingOff));
                bool block = r == pc.placeSquare || (r == pc.hoverSquare && pc.hoverSquare.sprite == pc.defaultHoverSquareSprite);
                if (FirstPerson.Active && voxels) PinFirstPerson(t, block);
                else
                {
                    float z = block && voxels ? VoxelTerrain.FrontZ - 0.02f * Scale : -0.02f * Scale;
                    t.position = new Vector3(t.position.x, t.position.y, z);
                }
                // First person: no highlight by default (user, 2026-10-07: first the item outline, then also the striped
                // block marker); FirstPerson.Highlight = Blocks brings back the block marker (mining and building aim),
                // All also the item outline. The diorama has its own switch.
                if (r == pc.hoverSquare && (FirstPerson.Active ? Plugin.FpHighlight.Value == "Off" || (!block && Plugin.FpHighlight.Value != "All") : !Plugin.HoverHighlight.Value))
                    r.forceRenderingOff = true;
                if (Plugin.Verbose && r == pc.hoverSquare && r.enabled && FirstPerson.Active && Time.unscaledTime >= nextMarkerLog)
                {
                    nextMarkerLog = Time.unscaledTime + 1f;
                    Plugin.Log.LogInfo($"Markers: {(block ? "block marker" : $"item outline '{pc.hoverSquare.sprite?.name}'")} {(r.forceRenderingOff ? "hidden" : "shown")} in first person");
                }
            }
            // Block cracks: on the block's front face (diorama), or on the face first person sees.
            var world = WorldGeneration.world;
            if (world == null || world.blockDamages == null) return;
            cracks.Clear();
            foreach (var bd in world.blockDamages)
            {
                var sr = bd != null ? CrackSprite(bd) : null;
                if (sr == null) continue;
                var t = sr.transform;
                pinned.Add((sr, t.position, t.rotation, sr.forceRenderingOff));
                if (FirstPerson.Active && voxels && DesktopFirstPerson.IsDesktopRenderActive)
                {
                    Vector3 normal = PinDesktopCrack(t);
                    if (Mathf.Abs(normal.x) > 0.5f)
                    {
                        crackDrawModes.Add((sr, sr.drawMode, sr.size));
                        sr.drawMode = SpriteDrawMode.Tiled;
                        Vector2 size = sr.size;
                        size.x = (VoxelTerrain.BackZ - VoxelTerrain.FrontZ) / Mathf.Max(0.001f, Mathf.Abs(t.localScale.x));
                        sr.size = size;
                    }
                }
                else if (FirstPerson.Active && voxels) PinFirstPerson(t, true, 0.56f);
                else t.position = new Vector3(t.position.x, t.position.y, voxels ? VoxelTerrain.FrontZ - 0.03f * Scale : -0.8f * Scale);
            }
        }

        private static Vector3 PinDesktopCrack(Transform t)
        {
            var cam = PlayerCamera.main != null ? PlayerCamera.main.GetComponent<Camera>() : null;
            if (cam == null)
            {
                PinFirstPerson(t, true, 0.56f);
                return Vector3.zero;
            }

            Vector3 center = t.position;
            Vector3 toCamera = cam.transform.position - center;
            Vector3 normal;
            float ax = Mathf.Abs(toCamera.x), ay = Mathf.Abs(toCamera.y), az = Mathf.Abs(toCamera.z);
            if (ax >= ay && ax >= az) normal = new Vector3(Mathf.Sign(toCamera.x), 0f, 0f);
            else if (ay >= az) normal = new Vector3(0f, Mathf.Sign(toCamera.y), 0f);
            else normal = new Vector3(0f, 0f, Mathf.Sign(toCamera.z));

            const float faceOffset = 0.52f;
            Vector3 position = center + normal * faceOffset;
            if (normal.z != 0f)
                position.z = normal.z < 0f ? VoxelTerrain.FrontZ - 0.02f * Scale : VoxelTerrain.BackZ + 0.02f * Scale;
            t.SetPositionAndRotation(position, Quaternion.FromToRotation(Vector3.back, normal));
            return normal;
        }

        /// <summary>
        /// First person: a block marker (hover square, building ghost, crack) goes onto the face of its block that
        /// the player sees: the face the aim ray entered if it is that block, else the side facing the player, at
        /// the aim's depth in the tunnel. Other markers (an item's outline) turn to face the player.
        /// </summary>
        private static void PinFirstPerson(Transform t, bool block, float faceOffset = 0.51f)
        {
            Vector3 eye = VrSpace.HeadPosition;
            Vector3 c = t.position;
            if (!block)
            {
                Vector3 d = eye - c;
                d.y = 0f;
                t.SetPositionAndRotation(new Vector3(c.x, c.y, 0f), d.sqrMagnitude < 1e-4f ? Quaternion.identity : Quaternion.FromToRotation(Vector3.back, d.normalized) * t.rotation);
                return;
            }
            var world = WorldGeneration.world;
            Vector3 n;
            float z = Mathf.Clamp(VrInput.LaserEnd.z, VoxelTerrain.FrontZ + 0.5f, VoxelTerrain.BackZ - 0.5f);
            if (world != null && VrInput.HitBlock && world.WorldToBlockPos(c) == VrInput.HitBlockPos) n = VrInput.HitNormal;
            else
            {
                n = new Vector3(Mathf.Sign(eye.x - c.x), 0f, 0f);
                z = 0f;
            }
            Quaternion rot = n.x != 0f ? Quaternion.Euler(0f, n.x > 0f ? -90f : 90f, 0f) : Quaternion.Euler(n.y > 0f ? 90f : -90f, 0f, 0f);
            t.SetPositionAndRotation(new Vector3(c.x, c.y, z) + n * faceOffset, rot);
        }

        /// <summary>
        /// Never move a transform that carries a Collider2D. Moving one, even for a single render and
        /// restoring it before physics runs, made Physics2D resync/rebuild the shapes: 90 -> 10 fps with
        /// both eyes (measured 2026-10-03, tools/vr-perf-matrix.ps1). Those objects (terrain chunks,
        /// limbs, items) sit at or near z=0 anyway.
        /// </summary>
        public static bool SkipColliders = true;
        public static int LastSkipped;
    }
}
