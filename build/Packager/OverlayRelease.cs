using UnityLudometry.Protocol.Json;

namespace UnityRuntimeAnalysisAgent.Packaging;

/// <summary>
/// The overlay's binary assets (asset bundles, the icon atlas) aren't in git: <c>assets/overlay/release.json</c> locks
/// them to a GitHub release of this repository (tag, SHA-256 and size per file). Before packaging, every locked file
/// must be present with its hash; a missing one is downloaded once from the release and verified, a changed one fails
/// the build. So building needs no Unity, and git holds only text.
/// </summary>
public static class OverlayRelease
{
    /// <summary>The lock file's name inside the overlay folder.</summary>
    public const string LockFile = "release.json";

    /// <summary>
    /// Makes sure every file in the lock is present and matches it, downloading missing ones with
    /// <paramref name="download"/> (given the file's release URL). Returns the files downloaded.
    /// </summary>
    /// <exception cref="InvalidOperationException">A file doesn't match the lock, or couldn't be downloaded.</exception>
    public static IReadOnlyList<string> Ensure(string overlayDir, Func<Uri, byte[]> download)
    {
        var lockPath = Path.Combine(overlayDir, LockFile);
        if (!File.Exists(lockPath))
        {
            return [];
        }

        var lockJson = (JsonObject)JsonValue.Parse(File.ReadAllText(lockPath));
        var repository = Text(lockJson, "repository");
        var tag = Text(lockJson, "tag");
        var fetched = new List<string>();
        foreach (var entry in (JsonObject)lockJson["files"]!)
        {
            var relative = entry.Key;
            var record = (JsonObject)entry.Value;
            var expected = Text(record, "sha256");
            var path = Path.Combine(overlayDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                if (PackageBuilder.Sha256(File.ReadAllBytes(path)) != expected)
                {
                    throw new InvalidOperationException(
                        $"{relative} doesn't match {LockFile}. If you rebuilt it, run tools/OverlayAssets/release.ps1 to update the lock and publish it; otherwise delete it to download the released one.");
                }

                continue;
            }

            var url = new Uri($"https://github.com/{repository}/releases/download/{tag}/{Path.GetFileName(relative)}");
            byte[] bytes;
            try
            {
                bytes = download(url);
            }
            catch (Exception e) when (e is not InvalidOperationException)
            {
                throw new InvalidOperationException($"{relative} is missing and couldn't be downloaded from {url}: {e.Message}", e);
            }

            if (PackageBuilder.Sha256(bytes) != expected)
            {
                throw new InvalidOperationException($"The downloaded {relative} ({url}) doesn't match {LockFile}; not used.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            fetched.Add(relative);
        }

        return fetched;
    }

    /// <summary>Downloads over HTTPS (the packager's default).</summary>
    public static byte[] HttpDownload(Uri url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UnityRuntimeAnalysisAgent-Packager");
        return client.GetByteArrayAsync(url).GetAwaiter().GetResult();
    }

    private static string Text(JsonObject json, string name) => (json[name] as JsonString)?.Value
        ?? throw new InvalidOperationException($"{LockFile} has no '{name}'.");
}
