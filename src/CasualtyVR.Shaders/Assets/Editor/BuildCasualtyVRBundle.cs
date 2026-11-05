using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Batch mode: Unity.exe -batchmode -quit -projectPath <this> -executeMethod BuildCasualtyVRBundle.Build
// Builds Bundles/casualtyvr_shaders (D3D11, Windows x64) with the CasualtyVR fog and flashlight variants of URP
// 14.0.12's 2D sprite shaders, for the game's own URP 14.0.12.
public static class BuildCasualtyVRBundle
{
    public static void Build()
    {
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        Directory.CreateDirectory("Bundles");
        var build = new AssetBundleBuild
        {
            assetBundleName = "casualtyvr_shaders",
            assetNames = new[]
            {
                "Assets/CasualtyVR/Shaders/Sprite-Lit-Fog.shader",
                "Assets/CasualtyVR/Shaders/Sprite-Unlit-Fog.shader",
            },
        };
        var manifest = BuildPipeline.BuildAssetBundles("Bundles", new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);
        Debug.Log(manifest != null ? "CasualtyVR bundle built: " + string.Join(", ", manifest.GetAllAssetBundles()) : "CasualtyVR bundle FAILED");
        foreach (var path in build.assetNames)
        {
            var sh = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Debug.Log($"CasualtyVR shader {path}: {(sh != null ? sh.name + " supported " + sh.isSupported : "missing")}; errors {ShaderUtil.GetShaderMessageCount(sh)}");
        }
    }
}
