using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using UnityLudometry.Protocol;
using UnityRuntimeAnalysisAgent.Api;
using UnityRuntimeAnalysisAgent.Core;
using UnityRuntimeAnalysisAgent.Overlay;
using UnityRuntimeAnalysisAgent.Unity;

namespace UnityRuntimeAnalysisAgent.Core.Tests;

/// <summary>
/// Enforces the assembly layering: loader- and engine-independent Core (which may use the protocol package),
/// dependency-free Api and protocol package.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string[] AllowedFrameworkAssemblies = ["netstandard", "mscorlib", "System", "System.Core"];

    [Fact]
    public void Core_does_not_reference_UnityEngine_or_any_loader()
    {
        var forbidden = typeof(AgentIdentity).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("UnityEngine", StringComparison.Ordinal)
                     || n.StartsWith("BepInEx", StringComparison.Ordinal)
                     || n.StartsWith("MelonLoader", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void Core_references_only_the_framework_the_protocol_package_and_compile_only_HarmonyX()
    {
        var references = typeof(AgentIdentity).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        var allowed = AllowedFrameworkAssemblies.Concat(["UnityLudometry.Protocol", "0Harmony"]).ToArray();

        Assert.All(references, name => Assert.Contains(name, allowed));
    }

    [Fact]
    public void The_protocol_package_references_only_netstandard()
    {
        var references = typeof(ProtocolVersion).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.Equal(["netstandard"], references);
    }

    [Fact]
    public void Api_references_only_the_framework()
    {
        var references = typeof(ApiInfo).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.All(references, name => Assert.Contains(name, AllowedFrameworkAssemblies));
    }

    [Fact]
    public void Shipped_assemblies_target_netstandard20()
    {
        foreach (var assembly in new[] { typeof(AgentIdentity).Assembly, typeof(ApiInfo).Assembly, typeof(ProtocolVersion).Assembly })
        {
            var framework = assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>();
            Assert.Equal(".NETStandard,Version=v2.0", framework?.FrameworkName);
        }
    }

    /// <summary>
    /// The agent compiles against the Unity 2018.1 API. <c>UnityEngine.Input</c> moved from CoreModule to
    /// InputLegacyModule in 2019.1, so a direct reference would fail with a TypeLoadException on newer players.
    /// Input must go through the reflection-bound accessor instead.
    /// </summary>
    [Fact]
    public void Unity_and_Overlay_never_reference_UnityEngine_Input()
    {
        foreach (var assembly in new[] { typeof(UnityRuntimeAnalysisAgent.Unity.ModuleBinder).Assembly, typeof(OverlayInfo).Assembly })
        {
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();

            var inputReferences = metadata.TypeReferences
                .Select(metadata.GetTypeReference)
                .Where(t => metadata.GetString(t.Namespace) == "UnityEngine" && metadata.GetString(t.Name) == "Input")
                .ToArray();

            Assert.True(inputReferences.Length == 0, $"{assembly.GetName().Name} references UnityEngine.Input directly.");
        }
    }
}
