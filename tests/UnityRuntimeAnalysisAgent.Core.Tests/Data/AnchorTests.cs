using System.Reflection;
using System.Runtime.Loader;
using dnlib.DotNet;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using Zoo;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Data;

/// <summary>
/// Anchors are exact: for every kind of member in the type zoo, the agent's <c>(mvid, token)</c> is the one dnlib reads
/// from the assembly file (which is what static tools produce), and resolving it gives back the identical member.
/// </summary>
public sealed class AnchorTests : IDisposable
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly string ZooPath = typeof(Plain).Assembly.Location;
    private readonly ModuleDefMD _dnlib = ModuleDefMD.Load(ZooPath);
    private readonly ModuleMap _modules = new();
    private readonly AnchorResolver _resolver;

    public AnchorTests() => _resolver = new AnchorResolver(_modules);

    public static TheoryData<string> Members => new()
    {
        "type Zoo.Plain", "type Zoo.Point", "type Zoo.Outer+Inner", "type Zoo.Outer+Inner+Deeper", "type Zoo.Box`1", "type Zoo.Access", "type Example.Inventory",
        "field Zoo.Plain number", "field Zoo.Plain text", "field Zoo.Plain StaticCount", "field Zoo.Plain <Auto>k__BackingField", "field Zoo.Box`1 item",
        "field Zoo.Outer+Inner value", "field Zoo.Derived shadowed", "field Zoo.Base shadowed",
        "method Zoo.Overloads Do()", "method Zoo.Overloads Do(System.Int32)", "method Zoo.Overloads Do(System.String)", "method Zoo.Overloads Do(System.Int32,System.String)",
        "method Zoo.Overloads .ctor()", "method Zoo.Overloads .ctor(System.Int32)", "method Zoo.Overloads .cctor()", "method Zoo.Box`1 Get()", "method Zoo.Box`1 Map(System.Func`2<T,TOut>)",
        "property Zoo.Plain Auto", "property Zoo.Plain StaticAuto", "property Zoo.WithEvents Property", "property Zoo.WithEvents Item",
        "event Zoo.WithEvents Changed", "event Zoo.WithEvents Counted",
    };

    [Theory]
    [MemberData(nameof(Members))]
    public void The_agents_anchor_is_dnlibs_and_resolves_back(string spec)
    {
        var (member, dnlibToken) = Find(spec);
        var anchor = AnchorWriter.ForMember(member);

        Assert.Equal(_dnlib.Mvid!.Value.ToString(), anchor.Mvid);
        Assert.Equal(dnlibToken, anchor.Token);
        Assert.Null(anchor.TypeArgs);
        Assert.Same(member, _resolver.ResolveMember(Roundtrip(anchor), "p"));
    }

    [Fact]
    public void Generic_instantiations_carry_their_arguments()
    {
        var boxOfInt = typeof(Box<int>);
        var typeAnchor = AnchorWriter.ForType(boxOfInt);
        Assert.Equal(["System.Int32"], typeAnchor.TypeArgs!.Select(a => ((JsonString)a).Value));
        Assert.Equal(boxOfInt, _resolver.ResolveType(Roundtrip(typeAnchor), "p"));

        var field = boxOfInt.GetField("item")!;
        var fieldAnchor = AnchorWriter.ForMember(field);
        Assert.Equal(typeof(Box<>).GetField("item")!.MetadataToken, fieldAnchor.Token);
        Assert.Equal(field, _resolver.ResolveMember(Roundtrip(fieldAnchor), "p"));

        // A game type as the argument is an anchor itself.
        var boxOfPlain = AnchorWriter.ForType(typeof(Box<Plain>));
        Assert.IsType<JsonObject>(boxOfPlain.TypeArgs![0]);
        Assert.Equal(typeof(Box<Plain>), _resolver.ResolveType(Roundtrip(boxOfPlain), "p"));

        var map = typeof(Box<int>).GetMethod("Map")!.MakeGenericMethod(typeof(string));
        var mapAnchor = AnchorWriter.ForMember(map);
        Assert.Equal(["System.String"], mapAnchor.MethodArgs!.Select(a => ((JsonString)a).Value));
        Assert.Equal(map, _resolver.ResolveMember(Roundtrip(mapAnchor), "p"));

        // A definition member binds to the runtime type it's read on.
        Assert.Equal(field, AnchorResolver.BindTo(typeof(Box<>).GetField("item")!, boxOfInt));
    }

    [Fact]
    public void Names_read_like_dnlibs()
    {
        Assert.Equal("Zoo.Outer/Inner", AnchorWriter.ForType(typeof(Outer.Inner)).Name);
        Assert.Equal("Zoo.Overloads::Do(System.Int32,System.String)", AnchorWriter.ForMember(typeof(Overloads).GetMethod("Do", [typeof(int), typeof(string)])!).Name);
        Assert.Equal("Zoo.Plain::number", AnchorWriter.ForMember(typeof(Plain).GetField("number")!).Name);
        Assert.Equal("Zoo.Box`1<System.Int32>", AnchorWriter.ForType(typeof(Box<int>)).Name);
        Assert.Equal($"code://UnityRuntimeAnalysisAgent.TestAssemblies@{_dnlib.Mvid}/{typeof(Plain).MetadataToken}", AnchorWriter.CodeLocator(typeof(Plain)));
    }

    [Fact]
    public void Stale_and_malformed_anchors_are_refused()
    {
        var unknown = new Anchor { Mvid = Guid.NewGuid().ToString(), Token = typeof(Plain).MetadataToken, Name = "Zoo.Plain" };
        var stale = Assert.Throws<ProtocolException>(() => _resolver.ResolveType(unknown, "p"));
        Assert.Equal(ErrorCodes.IndexStale, stale.Code);
        var builds = (JsonArray)stale.ErrorData!["loadedBuildsOfAssembly"]!;
        Assert.Contains(builds, b => ((JsonString)((JsonObject)b)["mvid"]!).Value == _dnlib.Mvid.ToString()); // informative: the build that is loaded
        Assert.Equal(unknown.Mvid, ((JsonString)((JsonObject)stale.ErrorData["anchor"]!)["mvid"]!).Value);

        var noSuchToken = new Anchor { Mvid = _dnlib.Mvid.ToString()!, Token = 0x02FFFFFF };
        Assert.Equal(ErrorCodes.IndexStale, Assert.Throws<ProtocolException>(() => _resolver.ResolveMember(noSuchToken, "p")).Code);

        var typeRefToken = new Anchor { Mvid = _dnlib.Mvid.ToString()!, Token = 0x01000001 };
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => _resolver.ResolveMember(typeRefToken, "p")).Code);

        var fieldAsType = AnchorWriter.ForMember(typeof(Plain).GetField("number")!);
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => _resolver.ResolveType(fieldAsType, "p")).Code);

        var wrongArity = AnchorWriter.ForType(typeof(Box<>));
        wrongArity.TypeArgs = [JsonValue.From("System.Int32"), JsonValue.From("System.Int32")];
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => _resolver.ResolveType(wrongArity, "p")).Code);

        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => AnchorResolver.Read(JsonValue.Parse("{\"mvid\":\"nope\",\"token\":1}"), "p")).Code);
    }

    [Fact]
    public void Primitive_and_BCL_types_can_be_named()
    {
        Assert.Equal(typeof(int), _resolver.ResolveTypeRef(JsonValue.From("System.Int32"), "p"));
        Assert.Equal(typeof(string[]), _resolver.ResolveTypeRef(JsonValue.From("System.String[]"), "p"));
        Assert.Equal(typeof(Plain), _resolver.ResolveTypeRef(AnchorWriter.ForType(typeof(Plain)).ToJson(), "p"));
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => _resolver.ResolveTypeRef(JsonValue.From("Zoo.Plain"), "p")).Code); // never by name
    }

    [Fact]
    public void Assemblies_loaded_later_are_mapped_and_a_second_copy_of_a_build_doesnt_replace_the_first()
    {
        var bytes = File.ReadAllBytes(ZooPath);
        var original = _dnlib.Mvid!.Value;
        var context = new AssemblyLoadContext("anchors", isCollectible: true);
        var rebuiltContext = new AssemblyLoadContext("anchors-rebuilt", isCollectible: true);
        try
        {
            // The same build again (same MVID): anchors stay bound to the copy already in use.
            context.LoadFromStream(new MemoryStream(bytes));
            Assert.True(_modules.TryGet(original, out var first));
            Assert.Same(typeof(Plain).Assembly, first.Assembly);

            // Another build (its MVID patched, as a rebuild would change it): mapped as it loads.
            var rebuilt = Guid.NewGuid();
            var at = IndexOf(bytes, original.ToByteArray());
            rebuilt.ToByteArray().CopyTo(bytes, at);
            Assert.Equal(-1, IndexOf(bytes, original.ToByteArray(), at + 1));
            var loaded = rebuiltContext.LoadFromStream(new MemoryStream(bytes));
            Assert.Equal(rebuilt, loaded.ManifestModule.ModuleVersionId);
            Assert.True(_modules.TryGet(rebuilt, out var module));
            Assert.Same(loaded, module.Assembly);
        }
        finally
        {
            context.Unload();
            rebuiltContext.Unload();
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        _modules.Dispose();
        _dnlib.Dispose();
    }

    // The same member found twice, independently: by reflection, and by dnlib from the file.
    private (MemberInfo Member, long Token) Find(string spec)
    {
        var parts = spec.Split(' ');
        var type = typeof(Plain).Assembly.GetType(parts[1])!;
        var typeDef = _dnlib.Find(parts[1], isReflectionName: true)!;
        switch (parts[0])
        {
            case "type":
                return (type, typeDef.MDToken.Raw);
            case "field":
                return (type.GetField(parts[2], All)!, typeDef.Fields.Single(f => f.Name == parts[2]).MDToken.Raw);
            case "property":
                return (type.GetProperty(parts[2], All)!, typeDef.Properties.Single(p => p.Name == parts[2]).MDToken.Raw);
            case "event":
                return (type.GetEvent(parts[2], All)!, typeDef.Events.Single(e => e.Name == parts[2]).MDToken.Raw);
            default:
                var name = parts[2][..parts[2].IndexOf('(')];
                var parameters = parts[2][(name.Length + 1)..^1];
                var method = type.GetMembers(All).OfType<MethodBase>().Single(m => m.Name == name && Signature(m) == parameters);
                var methodDef = typeDef.Methods.Single(m => m.Name == name && string.Join(",", m.MethodSig.Params.Select(p => p.FullName)) == parameters);
                return (method, methodDef.MDToken.Raw);
        }
    }

    private static string Signature(MethodBase method) => string.Join(",", method.GetParameters().Select(p => p.ParameterType.IsGenericType
        ? $"{p.ParameterType.GetGenericTypeDefinition().FullName}<{string.Join(",", p.ParameterType.GetGenericArguments().Select(a => a.Name))}>"
        : p.ParameterType.FullName ?? p.ParameterType.Name));

    // Anchors travel as JSON: resolve what a client would send back.
    private static Anchor Roundtrip(Anchor anchor) => AnchorResolver.Read(JsonValue.Parse(anchor.ToJson().ToString()), "p");
}
