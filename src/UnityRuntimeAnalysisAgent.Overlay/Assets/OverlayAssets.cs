using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityLudometry.Protocol.Json;

namespace UnityRuntimeAnalysisAgent.Overlay.Assets;

/// <summary>One bundle family in the overlay's <c>bundles/manifest.json</c>.</summary>
public sealed class BundleFamily
{
    /// <summary>Creates the record.</summary>
    public BundleFamily(string name, string file, string sha256, string builtWith)
    {
        Name = name;
        File = file;
        Sha256 = sha256;
        BuiltWith = builtWith;
    }

    /// <summary>The family: <c>2021.3</c>, <c>6000.3</c> or <c>legacy</c>.</summary>
    public string Name { get; }

    /// <summary>The bundle's file name.</summary>
    public string File { get; }

    /// <summary>Its SHA-256 (lowercase hex).</summary>
    public string Sha256 { get; }

    /// <summary>The Unity version it was built with.</summary>
    public string BuiltWith { get; }

    /// <summary>Whether it carries the UI Toolkit theme (every family but legacy).</summary>
    public bool HasUiToolkit => Name != "legacy";
}

/// <summary>
/// The overlay's assets next to the plugin (<c>overlay/</c>): the bundle manifest, and which family serves a game.
/// A bundle loads in its own Unity version and newer ones, and UI Toolkit's theme format changed in 6000.3, so:
/// <c>legacy</c> for 2018.1–2021.2 (uGUI only), <c>2021.3</c> for 2021.3–6000.2, <c>6000.3</c> for 6000.3 and newer.
/// </summary>
public sealed class OverlayAssets
{
    /// <summary>The oldest assets version this agent can use.</summary>
    public const int RequiredAssetsVersion = 1;

    private static readonly (string Family, int[] From)[] Families =
    {
        ("6000.3", new[] { 6000, 3 }),
        ("2021.3", new[] { 2021, 3 }),
        ("legacy", new[] { 2018, 1 }),
    };

    private OverlayAssets(string root, int assetsVersion, IReadOnlyList<BundleFamily> families)
    {
        Root = root;
        AssetsVersion = assetsVersion;
        BundleFamilies = families;
    }

    /// <summary>The <c>overlay/</c> folder.</summary>
    public string Root { get; }

    /// <summary>The assets version the bundles were built as.</summary>
    public int AssetsVersion { get; }

    /// <summary>The families present.</summary>
    public IReadOnlyList<BundleFamily> BundleFamilies { get; }

    /// <summary>Reads <c>bundles/manifest.json</c> under an overlay folder.</summary>
    /// <exception cref="InvalidDataException">No manifest, or one this agent can't use.</exception>
    public static OverlayAssets Load(string root)
    {
        var path = Path.Combine(Path.Combine(root, "bundles"), "manifest.json");
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"The overlay's bundle manifest is missing ({path}).");
        }

        var manifest = JsonValue.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("The overlay's bundle manifest isn't a JSON object.");
        var version = manifest["assetsVersion"] is JsonNumber n ? (int)n.GetDouble() : 0;
        if (version < RequiredAssetsVersion)
        {
            throw new InvalidDataException($"The overlay's assets are version {version}; this agent needs {RequiredAssetsVersion} or newer.");
        }

        var families = new List<BundleFamily>();
        if (manifest["families"] is JsonObject records)
        {
            foreach (var pair in records)
            {
                if (pair.Value is JsonObject r && r["file"] is JsonString file && r["sha256"] is JsonString sha)
                {
                    families.Add(new BundleFamily(pair.Key, file.Value, sha.Value, (r["builtWith"] as JsonString)?.Value ?? "?"));
                }
            }
        }

        return new OverlayAssets(root, version, families);
    }

    /// <summary>The family for a Unity version (e.g. <c>2022.3.10f1</c>), whether or not its bundle is present; null below 2018.1.</summary>
    public static string? FamilyFor(string unityVersion)
    {
        var parts = unityVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return null;
        }

        foreach (var (family, from) in Families)
        {
            if (major > from[0] || (major == from[0] && minor >= from[1]))
            {
                return family;
            }
        }

        return null;
    }

    /// <summary>The bundle serving a Unity version, if it's present; and why not, otherwise.</summary>
    public (BundleFamily? Family, string? Why) Pick(string unityVersion)
    {
        var name = FamilyFor(unityVersion);
        if (name is null)
        {
            return (null, $"Unity {unityVersion} is older than any overlay bundle (2018.1).");
        }

        var family = BundleFamilies.FirstOrDefault(f => f.Name == name);
        return family is null ? (null, $"The '{name}' overlay bundle (for Unity {unityVersion}) isn't installed.") : (family, null);
    }

    /// <summary>The bundle file's full path, after checking its SHA-256 against the manifest.</summary>
    /// <exception cref="InvalidDataException">The file is missing or changed.</exception>
    public string Verified(BundleFamily family)
    {
        var path = Path.Combine(Path.Combine(Root, "bundles"), family.File);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"The overlay bundle {family.File} is missing.");
        }

        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        if (actual != family.Sha256)
        {
            throw new InvalidDataException($"The overlay bundle {family.File} doesn't match its manifest (reinstall the agent).");
        }

        return path;
    }
}
