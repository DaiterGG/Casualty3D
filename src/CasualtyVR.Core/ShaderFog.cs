using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace CasualtyVR
{
    /// <summary>
    /// Custom first-person sprite shaders for distance fog and the desktop spotlight (docs/SHADERS.md).
    ///
    /// The bundle `casualtyvr_shaders` (built with Unity 2022.3.62f3 against the game's own URP 14.0.12 by
    /// tools/build-shaders.ps1) holds "CasualtyVR/Sprite-Lit-Fog" and "CasualtyVR/Sprite-Unlit-Fog": URP's own 2D sprite
    /// shaders, extended with fog and flashlight evaluation. While first-person fog or desktop flashlight support is
    /// active, materials using URP's Sprite-Lit-Default / Sprite-Unlit-Default are switched to these variants, then
    /// restored. Fog globals are set for headset eyes; desktop flashlight globals are set only during the monitor
    /// camera render.
    /// </summary>
    internal sealed class ShaderFog
    {
        private const string LitName = "Universal Render Pipeline/2D/Sprite-Lit-Default";
        private const string UnlitName = "Universal Render Pipeline/2D/Sprite-Unlit-Default";

        private static AssetBundle bundle;
        private static Shader litFog, unlitFog;
        private static bool loadTried;
        public static bool Available => litFog != null && unlitFog != null;

        private static readonly int FogColorId = Shader.PropertyToID("_CvrFogColor");
        private static readonly int FogParamsId = Shader.PropertyToID("_CvrFogParams");
        private static readonly int FlashPositionId = Shader.PropertyToID("_CvrFlashPosition");
        private static readonly int FlashDirectionId = Shader.PropertyToID("_CvrFlashDirection");
        private static readonly int FlashColorId = Shader.PropertyToID("_CvrFlashColor");
        private static readonly int FlashParamsId = Shader.PropertyToID("_CvrFlashParams");

        private readonly Dictionary<Material, Shader> swapped = new Dictionary<Material, Shader>();
        private int lastScan = -1000;
        private bool active;
        private bool fogRequested, flashlightMode;
        private Color color;
        private Vector4 parameters;

        public int SwappedCount => swapped.Count;

        public ShaderFog()
        {
            Load();
            RenderPipelineManager.beginCameraRendering += OnBegin;
            RenderPipelineManager.endCameraRendering += OnEnd;
        }

        private static void Load()
        {
            if (loadTried) return;
            loadTried = true;
            string path = Path.Combine(Path.GetDirectoryName(Plugin.NativeDir), "casualtyvr_shaders");
            if (!File.Exists(path)) { Plugin.Log.LogWarning("Shaders: bundle not found (" + path + "); first-person fog uses sheets"); return; }
            bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) { Plugin.Log.LogWarning("Shaders: bundle failed to load"); return; }
            foreach (var sh in bundle.LoadAllAssets<Shader>())
            {
                if (sh.name == "CasualtyVR/Sprite-Lit-Fog") litFog = sh;
                else if (sh.name == "CasualtyVR/Sprite-Unlit-Fog") unlitFog = sh;
            }
            Plugin.Log.LogInfo($"Shaders: bundle loaded (lit fog {(litFog != null && litFog.isSupported ? "ok" : "missing")}, unlit fog {(unlitFog != null && unlitFog.isSupported ? "ok" : "missing")})");
            if (litFog != null && !litFog.isSupported) litFog = null;
            if (unlitFog != null && !unlitFog.isSupported) unlitFog = null;
        }

        /// <summary>Per frame: fog wanted (amount > 0) with this colour, start and full distance (world units).</summary>
        public void Set(bool on, Color fogColor, float start, float end, float amount)
        {
            fogRequested = on;
            color = fogColor;
            parameters = new Vector4(start, Mathf.Max(start + 0.1f, end), Mathf.Clamp01(amount), 1f);
            UpdateActivation();
        }

        public void SetDesktopFlashlightMode(bool on)
        {
            flashlightMode = on;
            UpdateActivation();
        }

        private void UpdateActivation()
        {
            bool wanted = Available && (fogRequested || flashlightMode);
            if (wanted != active)
            {
                active = wanted;
                if (!active) RestoreAll();
                else lastScan = -1000;
            }
            if (active && Scans.Due(lastScan, 120, 8))
            {
                long t0 = Scans.Start();
                Scan();
                Scans.Stop("shader fog", t0);
            }
        }

        public static void SetFlashlightGlobals(Vector3 position, Vector3 direction, float range,
            float innerAngle, float outerAngle, float intensity, Color lightColor)
        {
            Shader.SetGlobalVector(FlashPositionId, position);
            Shader.SetGlobalVector(FlashDirectionId, direction.normalized);
            Shader.SetGlobalColor(FlashColorId, lightColor);
            Shader.SetGlobalVector(FlashParamsId, new Vector4(
                Mathf.Max(0f, range),
                Mathf.Cos(innerAngle * Mathf.Deg2Rad),
                Mathf.Cos(outerAngle * Mathf.Deg2Rad),
                Mathf.Max(0f, intensity)));
        }

        // New materials appear at runtime (per-limb instances, spawned things): a rescan every two seconds.
        private void Scan()
        {
            lastScan = Time.frameCount;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null || swapped.ContainsKey(m)) continue;
                string n = m.shader.name;
                if (n == LitName) { swapped[m] = m.shader; m.shader = litFog; }
                else if (n == UnlitName) { swapped[m] = m.shader; m.shader = unlitFog; }
            }
        }

        private void RestoreAll()
        {
            foreach (var kv in swapped) if (kv.Key != null && kv.Value != null) kv.Key.shader = kv.Value;
            swapped.Clear();
        }

        private void OnBegin(ScriptableRenderContext ctx, Camera cam)
        {
            if (!active || VrRig.Instance == null || !VrRig.Instance.IsEyeCamera(cam)) return;
            Shader.SetGlobalColor(FogColorId, color);
            Shader.SetGlobalVector(FogParamsId, parameters);
        }

        private void OnEnd(ScriptableRenderContext ctx, Camera cam)
        {
            Shader.SetGlobalVector(FogParamsId, Vector4.zero);   // the monitor's camera: no fog
        }

        public void Destroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBegin;
            RenderPipelineManager.endCameraRendering -= OnEnd;
            RestoreAll();
            Shader.SetGlobalVector(FogParamsId, Vector4.zero);
            SetFlashlightGlobals(Vector3.zero, Vector3.forward, 0f, 0f, 0f, 0f, Color.white);
        }
    }
}
