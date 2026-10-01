// Builds the overlay's asset bundle for this editor's version family (menu Overlay → Build Asset Bundles, or batch mode
// with -executeMethod OverlayAssetsBuilder.BuildFromCommandLine -outDir <dir>). It applies every setting itself:
// graphics APIs, font import settings, and which assets go in. Written for C# 7.3 so every supported editor compiles it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class OverlayAssetsBuilder
{
    private const string Root = "Assets/Overlay";

    [MenuItem("Overlay/Build Asset Bundles")]
    public static void BuildFromMenu()
    {
        // From the editor: the repository's bundle folder when the project sits in tools/OverlayAssets/Projects.
        var repoBundles = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", "..", "assets", "overlay", "bundles"));
        Build(Directory.Exists(Path.GetDirectoryName(repoBundles)) ? repoBundles : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Bundles")));
    }

    public static void BuildFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "-outDir");
        if (i < 0 || i + 1 >= args.Length)
        {
            throw new ArgumentException("[OverlayAssets] -outDir <dir> is required.");
        }

        Build(args[i + 1]);
    }

    /// <summary>The family this editor builds: 2021.3, 6000.3 or legacy (2018.4); null for any other version.</summary>
    public static string Family(string unityVersion)
    {
        if (unityVersion.StartsWith("2021.3.", StringComparison.Ordinal)) return "2021.3";
        if (unityVersion.StartsWith("6000.3.", StringComparison.Ordinal)) return "6000.3";
        if (unityVersion.StartsWith("2018.4.", StringComparison.Ordinal)) return "legacy";
        return null;
    }

    private static void Build(string outDir)
    {
        var family = Family(Application.unityVersion);
        if (family == null)
        {
            throw new InvalidOperationException("[OverlayAssets] Unity " + Application.unityVersion + " doesn't build a family (2021.3.x, 6000.3.x or 2018.4.x).");
        }

        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
            new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLCore });

        foreach (var path in AssetDatabase.FindAssets("t:Font", new[] { Root + "/Fonts" }).Select(g => AssetDatabase.GUIDToAssetPath(g)))
        {
            var importer = (TrueTypeFontImporter)AssetImporter.GetAtPath(path);
            var name = Path.GetFileName(path);
            var pixel = name.StartsWith("ark-pixel", StringComparison.Ordinal);
            importer.includeFontData = true;
            importer.fontTextureCase = FontTextureCase.Dynamic;
            importer.fontRenderingMode = pixel ? FontRenderingMode.HintedRaster : FontRenderingMode.Smooth;
            importer.fontSize = pixel ? (name.Contains("10px") ? 10 : 12) : 16;
            // A pixel of padding around every glyph: raster glyphs packed edge to edge bleed into each other in uGUI
            // wherever a glyph quad isn't exactly pixel-aligned.
            importer.characterPadding = 1;
            importer.SaveAndReimport();
        }

        // Fonts and shaders for every family; the UI Toolkit theme only where UI Toolkit runs (not legacy).
        var folders = new List<string> { Root + "/Fonts", Root + "/Shaders" };
        if (family != "legacy")
        {
            folders.Add(Root + "/UIToolkit");
        }

        var assets = AssetDatabase.FindAssets("", folders.ToArray()).Select(g => AssetDatabase.GUIDToAssetPath(g))
            .Where(p => !AssetDatabase.IsValidFolder(p)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Directory.CreateDirectory(outDir);
        var build = new AssetBundleBuild { assetBundleName = family + ".bundle", assetNames = assets };
        var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            throw new InvalidOperationException("[OverlayAssets] The bundle build failed; see the errors above.");
        }

        // Unity's own side files aren't shipped.
        foreach (var extra in new[] { build.assetBundleName + ".manifest", Path.GetFileName(outDir), Path.GetFileName(outDir) + ".manifest" })
        {
            var file = Path.Combine(outDir, extra);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        var bundle = Path.Combine(outDir, build.assetBundleName);
        string hash;
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(bundle))
        {
            hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        // One record per family; build.ps1 combines them into manifest.json.
        File.WriteAllText(Path.Combine(outDir, family + ".json"),
            "{\"family\":\"" + family + "\",\"file\":\"" + build.assetBundleName + "\",\"sha256\":\"" + hash + "\",\"bytes\":" + new FileInfo(bundle).Length
            + ",\"builtWith\":\"" + Application.unityVersion + "\",\"assets\":[" + string.Join(",", assets.Select(a => "\"" + a.Substring(Root.Length + 1) + "\"").ToArray()) + "]}");
        Debug.Log("[OverlayAssets] Done: " + build.assetBundleName + " (" + new FileInfo(bundle).Length / 1024 + " KB), " + assets.Length + " assets.");
    }
}
