using UnityRuntimeAnalysisAgent.Packaging;

// Usage: Packager --plugin-dir <BepInEx5 build output> --dist <dist folder> --version <version> --api <major.minor> [--commit <sha>]
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2)
{
    options[args[i]] = args[i + 1];
}

string Required(string name) => options.TryGetValue(name, out var value) && value.Length > 0
    ? value
    : throw new ArgumentException($"{name} is required.");

try
{
    var manifest = PackageBuilder.Build(new PackageInput(
        Required("--plugin-dir"), Required("--dist"), Required("--version"), Required("--api"), options.GetValueOrDefault("--commit")));
    Console.WriteLine($"Packaged UnityRuntimeAnalysisAgent {manifest.Version} ({manifest.Files.Count} files) into {Path.GetFullPath(Required("--dist"))}.");
    return 0;
}
catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}
