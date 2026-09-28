using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;
using UnityRuntimeAnalysisAgent.Packaging;
using UnityRuntimeAnalysisAgent.Unity;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Loader;

public sealed class LoaderAndPackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uraa-tests", Guid.NewGuid().ToString("N"));

    private static string Metadata(string key) => typeof(LoaderAndPackageTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    // --- the plugin -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_plugin_declares_its_GUID_name_and_a_numeric_version()
    {
        using var module = ModuleDefMD.Load(Metadata("PluginAssembly"));
        var plugin = module.Types.Single(t => t.FullName == "UnityRuntimeAnalysisAgent.BepInEx5.AgentPlugin");
        Assert.Equal("BepInEx.BaseUnityPlugin", plugin.BaseType.FullName);
        var attribute = plugin.CustomAttributes.Single(a => a.TypeFullName == "BepInEx.BepInPlugin");
        var args = attribute.ConstructorArguments.Select(a => a.Value.ToString()).ToList();
        Assert.Equal(AgentIdentity.PluginGuid, args[0]);
        Assert.Equal(AgentIdentity.PluginName, args[1]);
        Assert.Equal(Metadata("VersionPrefix"), args[2]);
        Assert.True(Version.TryParse(args[2], out _), "BepInEx parses the plugin version with System.Version");
    }

    [Fact]
    public void The_shim_never_references_UnityEngine_Input()
    {
        using var stream = File.OpenRead(Metadata("PluginAssembly"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        Assert.DoesNotContain(metadata.TypeReferences.Select(metadata.GetTypeReference),
            t => metadata.GetString(t.Namespace) == "UnityEngine" && metadata.GetString(t.Name) == "Input");
    }

    // --- config keys ------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_setting_is_listed_once_with_a_usable_default()
    {
        var keys = ConfigKeys.All;
        Assert.Equal(keys.Count, keys.Select(k => k.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(key.Description), key.Key);
            switch (key.Kind)
            {
                case ConfigKind.Bool:
                    Assert.True(bool.TryParse(key.Default, out _), key.Key);
                    break;
                case ConfigKind.Int:
                    Assert.True(int.TryParse(key.Default, System.Globalization.CultureInfo.InvariantCulture, out _), key.Key);
                    break;
                case ConfigKind.Float:
                    Assert.True(float.TryParse(key.Default, System.Globalization.CultureInfo.InvariantCulture, out _), key.Key);
                    break;
                case ConfigKind.Choice:
                    Assert.Contains(key.Default, key.Choices);
                    break;
            }
        }
    }

    [Fact]
    public void The_defaults_are_the_agents_own_defaults()
    {
        var source = new DictionaryConfigSource();
        foreach (var key in ConfigKeys.All)
        {
            source.Set(key.Key, key.Default);
        }

        var fromDefaults = AgentConfig.Read(source);
        var builtIn = AgentConfig.Read(new DictionaryConfigSource());
        Assert.Empty(fromDefaults.Warnings);
        Assert.Equal(builtIn.Limits(), fromDefaults.Limits());
        Assert.Equal((builtIn.Mode, builtIn.Transport, builtIn.LogLevel, builtIn.ProvidersDir), (fromDefaults.Mode, fromDefaults.Transport, fromDefaults.LogLevel, fromDefaults.ProvidersDir));
        foreach (var pair in builtIn.Limits())
        {
            Assert.Contains(ConfigKeys.All, k => k.Key == pair.Key);
        }
    }

    [Fact]
    public void The_configuration_guide_documents_every_setting()
    {
        var guide = File.ReadAllText(Path.Combine(Metadata("RepoRoot"), "docs", "configuration.md"));
        foreach (var key in ConfigKeys.All)
        {
            Assert.Contains($"| `{key.Key}` |", guide);
        }
    }

    // --- packaging --------------------------------------------------------------------------------------------------

    private string FakeBuild(params string[] extra)
    {
        var dir = Path.Combine(_dir, "build-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        foreach (var name in PackageBuilder.ShippedAssemblies.Concat(extra))
        {
            File.WriteAllText(Path.Combine(dir, name), $"contents of {name}");
        }

        File.WriteAllText(Path.Combine(dir, "UnityRuntimeAnalysisAgent.Core.pdb"), "symbols are not shipped");
        return dir;
    }

    [Fact]
    public void The_package_is_reproducible_and_its_manifest_matches_its_files()
    {
        var build = FakeBuild();
        var first = PackageBuilder.Build(new PackageInput(build, Path.Combine(_dir, "dist1"), "1.2.3-dev", "0.1", "abc123"));
        PackageBuilder.Build(new PackageInput(build, Path.Combine(_dir, "dist2"), "1.2.3-dev", "0.1", "abc123"));

        var zip1 = File.ReadAllBytes(Path.Combine(_dir, "dist1", "UnityRuntimeAnalysisAgent-1.2.3-dev-bepinex5.zip"));
        var zip2 = File.ReadAllBytes(Path.Combine(_dir, "dist2", "UnityRuntimeAnalysisAgent-1.2.3-dev-bepinex5.zip"));
        Assert.Equal(zip1, zip2);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "dist1", "package.json")), File.ReadAllBytes(Path.Combine(_dir, "dist2", "package.json")));

        Assert.Equal(6, first.Files.Count);
        foreach (var file in first.Files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(_dir, "dist1", file.Path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(PackageBuilder.Sha256(bytes), file.Sha256);
            Assert.Equal(bytes.Length, file.Size);
        }

        Assert.Equal(("bepinex5", "mono", "2018.1", "abc123"), (first.Loader, first.Runtime, first.UnityMin, first.GitCommit));
        Assert.Equal(ProtocolVersion.Major, first.Protocol.Major);
        using var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(_dir, "dist1", "UnityRuntimeAnalysisAgent-1.2.3-dev-bepinex5.zip"));
        Assert.Equal(7, archive.Entries.Count);
        Assert.DoesNotContain(archive.Entries, e => e.Name.EndsWith(".pdb", StringComparison.Ordinal));
    }

    [Fact]
    public void A_third_party_or_missing_assembly_fails_the_package()
    {
        var withHarmony = FakeBuild("0Harmony.dll");
        var error = Assert.Throws<InvalidOperationException>(() => PackageBuilder.Build(new PackageInput(withHarmony, Path.Combine(_dir, "d"), "1.0.0", "0.1", null)));
        Assert.Contains("0Harmony.dll", error.Message);

        var incomplete = FakeBuild();
        File.Delete(Path.Combine(incomplete, "UnityRuntimeAnalysisAgent.Api.dll"));
        Assert.Contains("missing", Assert.Throws<InvalidOperationException>(() => PackageBuilder.Build(new PackageInput(incomplete, Path.Combine(_dir, "e"), "1.0.0", "0.1", null))).Message);
    }

    // --- self-test and binders --------------------------------------------------------------------------------------

    [Fact]
    public void The_self_test_checks_the_pump_the_transport_discovery_and_the_config()
    {
        using var good = new TestHost();
        var result = AgentSelfTestResult.Read(good.CallStepping(good.Connect(), Methods.AgentSelfTest).Result, "result");
        Assert.True(result.Passed, string.Join("; ", result.Checks.Select(c => $"{c.Name}: {c.Message}")));
        Assert.Equal(new[] { "pump.roundTrip", "transport.listening", "discovery.published", "config.valid" }, result.Checks.Select(c => c.Name));

        using var misconfigured = new TestHost(configure: c => c.Set(AgentConfig.StallMsKey, "soon"));
        var failed = AgentSelfTestResult.Read(misconfigured.CallStepping(misconfigured.Connect(), Methods.AgentSelfTest).Result, "result");
        Assert.False(failed.Passed);
        Assert.False(failed.Checks.Single(c => c.Name == "config.valid").Passed);
    }

    private sealed class PresentBinder() : ModuleBinder("strings")
    {
        public Func<string, string>? Trim { get; private set; }

        protected override void Bind()
        {
            var type = RequireType("System.String", "System.Private.CoreLib");
            Trim = RequireMethod<Func<string, string>>(type, nameof(string.Trim), BindingFlags.Public | BindingFlags.Instance);
            Version = "1";
        }
    }

    private sealed class MissingBinder() : ModuleBinder("addressables")
    {
        protected override void Bind() => RequireType("UnityEngine.AddressableAssets.Addressables");
    }

    [Fact]
    public void Binders_bind_by_name_once_and_report_what_is_missing()
    {
        var capabilities = new CapabilitySet();
        var present = new PresentBinder();
        present.Report(capabilities);
        Assert.True(present.Available);
        Assert.Equal("x", present.Trim!("  x "));
        Assert.True(capabilities.IsAvailable("module:strings"));

        var missing = new MissingBinder();
        missing.Report(capabilities);
        Assert.False(missing.EnsureBound());
        Assert.Contains("UnityEngine.AddressableAssets.Addressables isn't loaded", missing.Reason);
        var module = capabilities.Modules().Single(m => m.Name == "addressables");
        Assert.False(module.Available);
        Assert.Equal(missing.Reason, module.Reason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
