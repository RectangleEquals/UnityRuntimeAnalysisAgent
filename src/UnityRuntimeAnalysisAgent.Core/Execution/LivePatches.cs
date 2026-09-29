using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;

namespace UnityRuntimeAnalysisAgent.Core.Execution;

/// <summary>
/// Live patch sets: the Harmony patch classes of an assembly the client compiled, applied under their own Harmony id
/// (<c>ulm.livepatch.&lt;patchSetId&gt;</c>) so reverting removes exactly that set. Never persisted; all are reverted when
/// the agent stops. Also reads the process's whole Harmony landscape (every owner's patches).
/// </summary>
internal sealed class LivePatchManager
{
    public const string IdPrefix = "ulm.livepatch.";

    private readonly AssemblyLoader _loader;
    private readonly Dictionary<string, PatchSet> _sets = new(StringComparer.Ordinal);
    private int _next;

    public LivePatchManager(AssemblyLoader loader) => _loader = loader;

    /// <summary>Methods patched by live patch sets.</summary>
    public int PatchedMethods
    {
        get
        {
            lock (_sets)
            {
                return _sets.Values.SelectMany(s => s.Targets).Distinct().Count();
            }
        }
    }

    /// <summary>Applies the patch classes (main thread). Failures of single classes or targets are listed in <c>errors</c>;
    /// the set is kept if anything was patched.</summary>
    public (PatchApplyResult Result, LoadedAssembly Assembly) Apply(PatchApplyParams p, string owner)
    {
        var id = p.Id;
        lock (_sets)
        {
            if (string.IsNullOrEmpty(id))
            {
                do
                {
                    id = "p-" + (++_next).ToString(CultureInfo.InvariantCulture);
                }
                while (_sets.ContainsKey(id));
            }
            else if (_sets.ContainsKey(id!))
            {
                throw ProtocolException.InvalidParams("params.id", $"The patch set {id} is already applied; revert it first or use another id.");
            }
        }

        var assembly = _loader.Load(AssemblyLoader.Decode(p.Assembly, "params.assembly"));
        var types = PatchTypes(assembly, p.Types);
        var harmony = new Harmony(IdPrefix + id);
        var errors = new List<PatchError>();
        foreach (var type in types)
        {
            var before = new HashSet<MethodBase>(harmony.GetPatchedMethods());
            try
            {
                harmony.CreateClassProcessor(type, allowUnannotatedType: p.Types is not null).Patch();
            }
            catch (Exception e)
            {
                var inner = AgentErrors.Unwrap(e);
                var message = $"{type.FullName}: {inner.GetType().Name}: {inner.Message}";
                for (var cause = inner.InnerException; cause is not null; cause = cause.InnerException)
                {
                    message += $" ← {cause.GetType().Name}: {cause.Message}";
                }

                // Harmony keeps a failed patch registered (every later patch of the method would fail on it): the class's
                // patches are removed again, and the error names what they targeted.
                var touched = harmony.GetPatchedMethods().Where(m => !before.Contains(m)).ToList();
                foreach (var method in touched)
                {
                    try
                    {
                        harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
                    }
                    catch (Exception)
                    {
                        message += $" (its patch on {AnchorWriter.MemberName(method)} couldn't be removed)";
                    }
                }

                errors.Add(new PatchError { Target = touched.Where(Anchorable).Select(AnchorWriter.ForMember).FirstOrDefault(), Message = message });
            }
        }

        var targets = harmony.GetPatchedMethods().Where(t => Harmony.GetPatchInfo(t)?.Owners.Contains(harmony.Id) == true).ToList();
        if (targets.Count > 0)
        {
            lock (_sets)
            {
                _sets[id!] = new PatchSet(id!, harmony, targets, owner, DateTime.UtcNow);
            }
        }
        else if (errors.Count == 0)
        {
            errors.Add(new PatchError { Message = "Nothing was patched: the assembly has no [HarmonyPatch] classes (or the named types patch nothing)." });
        }

        return (new PatchApplyResult
        {
            PatchSetId = id!,
            Patched = targets.Select(t => Counts(t, harmony.Id)).ToList(),
            Errors = errors,
        }, assembly);
    }

    /// <summary>Removes a patch set's patches (main thread); <c>NOT_FOUND</c> if there's no such set.</summary>
    public PatchRevertResult Revert(string patchSetId)
    {
        PatchSet? set;
        lock (_sets)
        {
            if (!_sets.TryGetValue(patchSetId, out set))
            {
                throw AgentErrors.NotFound($"No patch set {patchSetId}.");
            }

            _sets.Remove(patchSetId);
        }

        try
        {
            set.Harmony.UnpatchSelf();
        }
        catch (Exception e)
        {
            throw new ProtocolException(ErrorCodes.PatchFailed, $"Reverting {patchSetId} failed: {e.Message}");
        }

        return new PatchRevertResult { Unpatched = set.Targets.Where(Anchorable).Select(AnchorWriter.ForMember).ToList() };
    }

    public PatchListResult List()
    {
        lock (_sets)
        {
            return new PatchListResult
            {
                Items = _sets.Values.OrderBy(s => s.AppliedAt).Select(s => new PatchSetInfo
                {
                    PatchSetId = s.Id,
                    Targets = s.Targets.Where(Anchorable).Select(AnchorWriter.ForMember).ToList(),
                    AppliedAt = s.AppliedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    Owner = s.Owner,
                }).ToList(),
            };
        }
    }

    /// <summary>Reverts every set (at shutdown); returns how many there were.</summary>
    public int RevertAll()
    {
        List<PatchSet> sets;
        lock (_sets)
        {
            sets = _sets.Values.ToList();
            _sets.Clear();
        }

        foreach (var set in sets)
        {
            try
            {
                set.Harmony.UnpatchSelf();
            }
            catch (Exception)
            {
                // Shutting down: nothing more can be done for this set.
            }
        }

        return sets.Count;
    }

    /// <summary>Every Harmony patch on a method, whoever owns it.</summary>
    public static List<HarmonyPatchInfo> Inspect(MethodBase method)
    {
        var info = Harmony.GetPatchInfo(method);
        var patches = new List<HarmonyPatchInfo>();
        if (info is null)
        {
            return patches;
        }

        void Add(IEnumerable<Patch> list, string kind) => patches.AddRange(list.Select(patch => new HarmonyPatchInfo
        {
            Owner = patch.owner,
            Kind = kind,
            Method = patch.PatchMethod is { } m && Anchorable(m) ? AnchorWriter.ForMember(m) : null,
            Priority = patch.priority,
            Before = patch.before is { Length: > 0 } before ? before.ToList() : null,
            After = patch.after is { Length: > 0 } after ? after.ToList() : null,
        }));

        Add(info.Prefixes, "prefix");
        Add(info.Postfixes, "postfix");
        Add(info.Transpilers, "transpiler");
        Add(info.Finalizers, "finalizer");
        return patches;
    }

    /// <summary>Every Harmony-patched method in the process with its patches (only the owner's, if given).</summary>
    public static List<PatchedMethod> All(string? owner) =>
        Harmony.GetAllPatchedMethods()
            .Where(Anchorable)
            .Select(m => new PatchedMethod { Method = AnchorWriter.ForMember(m), Patches = Inspect(m).Where(p => owner is null || p.Owner == owner).ToList() })
            .Where(m => m.Patches.Count > 0)
            .ToList();

    // Dynamic methods (no metadata token) can't be anchored; they are the ones without a declaring type.
    private static bool Anchorable(MethodBase method) => method.DeclaringType is not null;

    private static PatchedTarget Counts(MethodBase target, string owner)
    {
        var info = Harmony.GetPatchInfo(target);
        long Count(IEnumerable<Patch>? patches) => patches?.Count(p => p.owner == owner) ?? 0;
        return new PatchedTarget
        {
            Target = AnchorWriter.ForMember(target),
            Prefixes = Count(info?.Prefixes),
            Postfixes = Count(info?.Postfixes),
            Transpilers = Count(info?.Transpilers),
            Finalizers = Count(info?.Finalizers),
        };
    }

    private static List<Type> PatchTypes(LoadedAssembly assembly, List<string>? names)
    {
        Type[] all;
        try
        {
            all = assembly.Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            throw AgentErrors.ExecFailed("bind", $"The types of {assembly.Name} can't be loaded: {e.LoaderExceptions.FirstOrDefault()?.Message}", e);
        }

        if (names is null)
        {
            return all.Where(t => t.GetCustomAttributes(typeof(HarmonyAttribute), inherit: true).Length > 0).ToList();
        }

        return names.Select((name, i) => all.FirstOrDefault(t => t.FullName == name)
            ?? throw AgentErrors.ExecFailed("bind", $"{assembly.Name} has no type {name} (params.types[{i}]).")).ToList();
    }

    private sealed class PatchSet
    {
        public PatchSet(string id, Harmony harmony, List<MethodBase> targets, string owner, DateTime appliedAt)
        {
            Id = id;
            Harmony = harmony;
            Targets = targets;
            Owner = owner;
            AppliedAt = appliedAt;
        }

        public string Id { get; }

        public Harmony Harmony { get; }

        public List<MethodBase> Targets { get; }

        public string Owner { get; }

        public DateTime AppliedAt { get; }
    }
}
