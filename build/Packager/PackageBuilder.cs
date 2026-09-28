using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;

namespace UnityRuntimeAnalysisAgent.Packaging;

/// <summary>What the package is built from.</summary>
public sealed record PackageInput(string PluginBuildDir, string DistDir, string Version, string ApiVersion, string? GitCommit);

/// <summary>
/// Builds the release package from the BepInEx 5 plugin's build output:
/// <c>dist/BepInEx/plugins/UnityRuntimeAnalysisAgent/</c> (the six agent assemblies), <c>dist/package.json</c> (versions,
/// target, and the SHA-256 and size of every file), and <c>dist/UnityRuntimeAnalysisAgent-&lt;version&gt;-bepinex5.zip</c>.
/// The same input always gives byte-identical output. Any other assembly in the build output fails the build: the plugin
/// must never ship a third-party DLL (the game and the loader provide those).
/// </summary>
public static class PackageBuilder
{
    /// <summary>The assemblies the package ships, and only these.</summary>
    public static readonly IReadOnlyList<string> ShippedAssemblies =
    [
        "UnityLudometry.Protocol.dll",
        "UnityRuntimeAnalysisAgent.Api.dll",
        "UnityRuntimeAnalysisAgent.BepInEx5.dll",
        "UnityRuntimeAnalysisAgent.Core.dll",
        "UnityRuntimeAnalysisAgent.Overlay.dll",
        "UnityRuntimeAnalysisAgent.Unity.dll",
    ];

    /// <summary>Where the plugin goes inside a game (and inside the package).</summary>
    public const string PluginFolder = "BepInEx/plugins/UnityRuntimeAnalysisAgent";

    // A fixed entry time keeps the zip reproducible (the earliest time the zip format can store).
    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Builds the package and returns its manifest.</summary>
    public static PackageManifest Build(PackageInput input)
    {
        var present = Directory.EnumerateFiles(input.PluginBuildDir, "*.dll").Select(Path.GetFileName).OfType<string>().ToList();
        var unexpected = present.Except(ShippedAssemblies, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (unexpected.Count > 0)
        {
            throw new InvalidOperationException(
                $"The plugin build contains assemblies that must not ship: {string.Join(", ", unexpected)}. Third-party assemblies are referenced compile-only.");
        }

        var missing = ShippedAssemblies.Except(present, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"The plugin build is missing {string.Join(", ", missing)}.");
        }

        var pluginDist = Path.Combine(input.DistDir, PluginFolder.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(input.DistDir))
        {
            Directory.Delete(input.DistDir, recursive: true);
        }

        Directory.CreateDirectory(pluginDist);
        var files = new List<PackageFile>();
        foreach (var name in ShippedAssemblies)
        {
            var target = Path.Combine(pluginDist, name);
            File.Copy(Path.Combine(input.PluginBuildDir, name), target);
            var bytes = File.ReadAllBytes(target);
            files.Add(new PackageFile { Path = $"{PluginFolder}/{name}", Sha256 = Sha256(bytes), Size = bytes.Length });
        }

        var manifest = new PackageManifest
        {
            Name = "UnityRuntimeAnalysisAgent",
            Version = input.Version,
            ApiVersion = input.ApiVersion,
            Protocol = ProtocolVersionInfo.Current,
            Loader = "bepinex5",
            Runtime = "mono",
            UnityMin = "2018.1",
            Files = files,
            GitCommit = string.IsNullOrWhiteSpace(input.GitCommit) ? null : input.GitCommit,
        };
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJson().ToString() + "\n");
        File.WriteAllBytes(Path.Combine(input.DistDir, "package.json"), manifestBytes);

        var zipPath = Path.Combine(input.DistDir, $"UnityRuntimeAnalysisAgent-{input.Version}-bepinex5.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(zip, "package.json", manifestBytes);
            foreach (var name in ShippedAssemblies)
            {
                AddEntry(zip, $"{PluginFolder}/{name}", File.ReadAllBytes(Path.Combine(pluginDist, name)));
            }
        }

        return manifest;
    }

    /// <summary>Lowercase hex SHA-256.</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }
}
