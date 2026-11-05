using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace CasualtyVR
{
    /// <summary>
    /// Phase 4: liquids with depth. The game keeps liquid per block (FluidManager.fluid) and every 0.1 s
    /// redraws it as one particle quad per wet cell, all at z=0 (FluidManager.RenderFluids). With voxel
    /// terrain that is a flat sheet halfway into each tunnel. While VR and voxels are on:
    /// * the particles are moved to just behind the block front plane, so a pool fills its tunnel from the
    ///   front (anything in the water, the player included, is seen through it);
    /// * a translucent surface spans the tunnel depth on top of each pool (layer 25, eye-only), lit by the
    ///   2D lights like the terrain.
    /// The monitor's orthographic camera is unaffected by z. Turning off redraws the liquids at z=0.
    /// </summary>
    internal sealed class LiquidVolume
    {
        internal static LiquidVolume Instance;

        private static readonly AccessTools.FieldRef<FluidManager, List<ParticleSystem>> Particles =
            AccessTools.FieldRefAccess<FluidManager, List<ParticleSystem>>("liquidParticles");

        private readonly GameObject go;
        private readonly Mesh mesh;
        private readonly MeshRenderer mr;
        private Material mat;
        private ParticleSystem.Particle[] buffer = new ParticleSystem.Particle[1024];
        private readonly List<Vector3> verts = new List<Vector3>();
        private readonly List<Color> colors = new List<Color>();
        private readonly List<Vector2> uvs = new List<Vector2>();
        private readonly List<int> tris = new List<int>();
        private bool wasActive;
        private readonly HashSet<string> logged = new HashSet<string>();

        public int SurfaceCells { get; private set; }

        public static bool Active => Instance != null && !VrRig.DbgNoLiquids && Plugin.LiquidDepthOn.Value && Plugin.VoxelsOn.Value && PopupDepth.Scale > 0f;

        public LiquidVolume()
        {
            go = new GameObject("CasualtyVR.LiquidSurface") { layer = VoxelTerrain.Layer, hideFlags = HideFlags.DontSave };
            Object.DontDestroyOnLoad(go);
            mesh = new Mesh { name = "CasualtyVR.LiquidSurface", indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.sortingLayerName = "Top";
            mr.sortingOrder = -499;   // right after the game's liquid particles (Top/-500)
            Instance = this;
        }

        public void Update()
        {
            bool active = Active;
            if (active != wasActive)
            {
                wasActive = active;
                go.SetActive(active);
                // Redraw now, so the particles move to (or back from) the front plane straight away.
                if (FluidManager.main != null && FluidManager.main.fluid != null && WorldGeneration.world != null && PlayerCamera.main != null)
                    FluidManager.main.RenderFluids();
            }
        }

        /// <summary>Called after every FluidManager.RenderFluids (Harmony postfix).</summary>
        internal void AfterRender(FluidManager fm)
        {
            if (!Active) return;
            float z = VoxelTerrain.FrontZ + 0.05f * PopupDepth.Scale;   // just behind the block faces: blocks win the overlap
            var systems = Particles(fm);
            if (systems != null)
                foreach (var ps in systems)
                {
                    if (ps == null) continue;
                    // particleCount still reports the previous simulation step right after SetParticles;
                    // GetParticles returns what was just set.
                    int max = ps.main.maxParticles;
                    if (buffer.Length < max) buffer = new ParticleSystem.Particle[Mathf.NextPowerOfTwo(max)];
                    int n = ps.GetParticles(buffer, max);
                    if (n == 0) continue;
                    float localZ = ps.main.simulationSpace == ParticleSystemSimulationSpace.World ? z : z - ps.transform.position.z;
                    for (int i = 0; i < n; i++)
                    {
                        var p = buffer[i].position;
                        buffer[i].position = new Vector3(p.x, p.y, localZ);
                    }
                    ps.SetParticles(buffer, n);
                    if (Plugin.Verbose && logged.Add(ps.name))
                        Plugin.Log.LogInfo($"Liquids: '{ps.name}' {n} particles, space {ps.main.simulationSpace}, transform {ps.transform.position}, " +
                                           $"first {buffer[0].position} size {buffer[0].startSize3D}, renderer {ps.GetComponent<ParticleSystemRenderer>()?.sortingLayerName}/{ps.GetComponent<ParticleSystemRenderer>()?.sortingOrder} " +
                                           $"mat {ps.GetComponent<ParticleSystemRenderer>()?.sharedMaterial?.shader?.name} layer {ps.gameObject.layer}");
                }
            if (Plugin.Verbose && logged.Add("colors"))
                Plugin.Log.LogInfo("Liquids: colours " + string.Join(", ", System.Linq.Enumerable.Select(fm.liquidColors, c => c.ToString())));
            BuildSurface(fm);
        }

        private void BuildSurface(FluidManager fm)
        {
            var world = WorldGeneration.world;
            if (world == null) return;
            if (mat == null)
            {
                mat = new Material(world.defaultMat) { name = "CasualtyVR.LiquidSurface", mainTexture = Texture2D.whiteTexture };
                mr.sharedMaterial = mat;
            }
            verts.Clear(); colors.Clear(); uvs.Clear(); tris.Clear();
            float zf = VoxelTerrain.FrontZ + 0.05f * PopupDepth.Scale, zb = VoxelTerrain.BackZ;
            var (rx, ry) = fm.SimulationRange();
            var fluid = fm.fluid;
            float alpha = Plugin.LiquidSurfaceAlpha.Value;
            Color col;
            for (int j = rx.min; j < rx.max; j++)
                for (int k = ry.min; k < ry.max; k++)
                {
                    int type = fluid[j, k];
                    if (type == 0 || fluid[j, k + 1] != 0) continue;
                    if (world.GetBlock(new Vector2Int(j, k + 1)) != 0) continue;   // under a ceiling: no open surface
                    // Same rule as the game: a surface cell at a pool's edge is drawn half height.
                    bool edge = fluid[j + 1, k] == 0 || fluid[j - 1, k] == 0;
                    Vector2 c = world.BlockToWorldPos(new Vector2Int(j, k));
                    float y = c.y + (edge ? 0f : 0.5f);
                    col = SurfaceColor(fm, type);
                    col.a = alpha;
                    AddQuad(new Vector3(c.x - 0.5f, y, zf), new Vector3(c.x + 0.5f, y, zf),
                        new Vector3(c.x + 0.5f, y, zb), new Vector3(c.x - 0.5f, y, zb), col);
                    AddExposedSide(world, fluid, ry, j, k, -1, y, zf, zb, col);
                    AddExposedSide(world, fluid, ry, j, k, 1, y, zf, zb, col);
                }
            SurfaceCells = verts.Count / 4;
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0, true);
        }

        private void AddExposedSide(WorldGeneration world, byte[,] fluid, RangeI ry,
            int x, int surfaceCell, int direction, float surfaceY, float zf, float zb, Color color)
        {
            int neighborX = x + direction;
            if (neighborX <= 0 || neighborX >= fluid.GetLength(0) - 1) return;
            float lowerY = float.NegativeInfinity;
            bool foundNeighborSurface = false;
            for (int y = Mathf.Max(ry.min, surfaceCell - 1); y <= Mathf.Min(ry.max - 1, surfaceCell + 1); y++)
            {
                if (fluid[neighborX, y] == 0 || fluid[neighborX, y + 1] != 0) continue;
                bool edge = fluid[neighborX + 1, y] == 0 || fluid[neighborX - 1, y] == 0;
                float candidate = world.BlockToWorldPos(new Vector2Int(neighborX, y)).y + (edge ? 0f : 0.5f);
                if (!foundNeighborSurface || candidate > lowerY)
                {
                    lowerY = candidate;
                    foundNeighborSurface = true;
                }
            }

            // A missing neighboring surface is an open shoreline, not a vertical water wall.
            if (!foundNeighborSurface || lowerY >= surfaceY - 0.001f) return;

            float edgeX = world.BlockToWorldPos(new Vector2Int(x, surfaceCell)).x + direction * 0.5f;
            var sideColor = color;
            sideColor.a *= 0.8f;
            AddDoubleSidedQuad(new Vector3(edgeX, lowerY, zf), new Vector3(edgeX, surfaceY, zf),
                new Vector3(edgeX, surfaceY, zb), new Vector3(edgeX, lowerY, zb), sideColor);
        }

        private void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            int i = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            for (int q = 0; q < 4; q++)
            {
                colors.Add(color);
                uvs.Add(new Vector2(0.5f, 0.5f));
            }
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }

        private void AddDoubleSidedQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            AddQuad(a, b, c, d, color);
            AddQuad(d, c, b, a, color);
        }

        // FluidManager.liquidColors is the liquid's identity colour (water is white: its shader does the
        // blue), so water gets a water tint; the others are dimmed so a lit surface does not glow.
        private static readonly Color WaterTint = new Color(0.22f, 0.45f, 0.75f);

        private static Color SurfaceColor(FluidManager fm, int type)
        {
            Color c = type < fm.liquidColors.Length ? fm.liquidColors[type] : Color.white;
            if (c.r > 0.9f && c.g > 0.9f && c.b > 0.9f) return WaterTint;
            return new Color(c.r * 0.6f, c.g * 0.6f, c.b * 0.6f);
        }

        public void Destroy()
        {
            Instance = null;
            if (wasActive && FluidManager.main != null && FluidManager.main.fluid != null && WorldGeneration.world != null && PlayerCamera.main != null)
                FluidManager.main.RenderFluids();   // back to z=0
            Object.Destroy(mesh);
            if (mat != null) Object.Destroy(mat);
            Object.Destroy(go);
        }
    }

    [HarmonyPatch(typeof(FluidManager), nameof(FluidManager.RenderFluids))]
    internal static class FluidRenderPatch
    {
        private static void Postfix(FluidManager __instance)
        {
            LiquidVolume.Instance?.AfterRender(__instance);
        }
    }
}
