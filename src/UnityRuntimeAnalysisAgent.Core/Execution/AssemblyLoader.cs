using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;

namespace UnityRuntimeAnalysisAgent.Core.Execution;

/// <summary>An assembly the agent loaded from a client's bytes.</summary>
internal sealed class LoadedAssembly
{
    public LoadedAssembly(Assembly assembly, string name, string sha256)
    {
        Assembly = assembly;
        Name = name;
        Sha256 = sha256;
    }

    public Assembly Assembly { get; }

    public string Name { get; }

    public string Sha256 { get; }

    public AuditedAssembly Audit => new() { Name = Name, Sha256 = Sha256 };
}

/// <summary>
/// Loads snippet, patch and mod assemblies from bytes (no file stays locked). Mono can't unload assemblies, and
/// <c>Assembly.Load(byte[])</c> may hand back an already loaded assembly of the same identity, so a name that is already
/// loaded is refused (<c>DUPLICATE_ASSEMBLY</c>); every build needs a unique name. Loads are counted, with a warning once
/// the count passes <see cref="WarnAt"/> and every <see cref="WarnEvery"/> after.
/// </summary>
internal sealed class AssemblyLoader
{
    public const int WarnAt = 300;
    public const int WarnEvery = 100;

    private readonly Action<string, string, JsonObject> _warning;
    private int _loaded;

    public AssemblyLoader(Action<string, string, JsonObject> warning) => _warning = warning;

    /// <summary>Assemblies loaded so far.</summary>
    public int Loaded => Volatile.Read(ref _loaded);

    /// <summary>Decodes base64 assembly bytes (<c>INVALID_PARAMS</c> if they aren't).</summary>
    public static byte[] Decode(string base64, string param)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw ProtocolException.InvalidParams(param, "The assembly must be base64.");
        }
    }

    /// <summary>Loads the assembly; <c>EXEC_FAILED {phase: load}</c> if it isn't one, <c>DUPLICATE_ASSEMBLY</c> if its name is
    /// already loaded.</summary>
    public LoadedAssembly Load(byte[] bytes)
    {
        var name = ReadName(bytes);
        var clash = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(SafeName(a), name.Name, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            throw new ProtocolException(ErrorCodes.DuplicateAssembly,
                $"An assembly named {name.Name} is already loaded ({clash.FullName}); give every build a unique assembly name.",
                AgentErrors.Data(("assembly", new JsonString(name.Name ?? string.Empty))));
        }

        Assembly assembly;
        try
        {
            assembly = Assembly.Load(bytes);
        }
        catch (Exception e)
        {
            throw AgentErrors.ExecFailed("load", $"Loading {name.Name} failed: {e.Message}", e);
        }

        var count = Interlocked.Increment(ref _loaded);
        if (count >= WarnAt && (count - WarnAt) % WarnEvery == 0)
        {
            _warning("ASSEMBLIES_ACCUMULATING",
                $"The agent has loaded {count} assemblies into the game; the runtime can't unload them, so memory grows until the game restarts.",
                AgentErrors.Data(("loaded", new JsonNumber((long)count))));
        }

        return new LoadedAssembly(assembly, name.Name ?? assembly.GetName().Name ?? "?", Sha256(bytes));
    }

    public static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    // The name without loading the assembly: AssemblyName.GetAssemblyName reads a file's metadata only.
    private static AssemblyName ReadName(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "uraa-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.WriteAllBytes(path, bytes);
            return AssemblyName.GetAssemblyName(path);
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException)
        {
            throw AgentErrors.ExecFailed("load", "The bytes aren't a .NET assembly.", e);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless.
            }
        }
    }

    private static string? SafeName(Assembly assembly)
    {
        try
        {
            return assembly.GetName().Name;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
