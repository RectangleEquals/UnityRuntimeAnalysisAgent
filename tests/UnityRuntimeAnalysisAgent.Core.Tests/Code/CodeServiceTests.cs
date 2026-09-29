using System.Reflection;
using System.Security.Cryptography;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;
using Zoo;
using Zoo.Il;
using Zoo.Survey;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Code;

/// <summary>Code introspection over the wire: assemblies, types, members, hierarchy, attributes, IL and cross-references.
/// Name-based queries cover every loaded build, and other tests load copies of the zoo, so results are narrowed to the zoo's
/// own build (<see cref="ZooMvid"/>) where that matters.</summary>
public sealed class CodeServiceTests : IDisposable
{
    private static readonly string Zoo = "UnityRuntimeAnalysisAgent.TestAssemblies";
    private static readonly string ZooMvid = typeof(Plain).Module.ModuleVersionId.ToString();
    private readonly TestHost _test = new();
    private readonly WirePeer _peer;

    public CodeServiceTests()
    {
        _peer = _test.Connect();
        _ = typeof(Broken.Fine).Assembly; // loaded, so the agent sees it
    }

    [Fact]
    public void Assemblies_with_their_file_hash_and_origin()
    {
        var all = CodeAssembliesResult.Read(Call(Methods.CodeAssemblies, "{}"), "result").Items;
        var zoo = all.Single(a => a.Mvid == ZooMvid);
        Assert.Equal(Zoo, zoo.Name);
        Assert.Equal(typeof(Plain).Module.ModuleVersionId.ToString(), zoo.Mvid);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(Plain).Assembly.Location))), zoo.FileSha256);
        Assert.False(zoo.LoadedFromBytes);
        Assert.False(zoo.LoadedByAgent);
        Assert.True(zoo.TypeCount > 50);
        Assert.True(all.Single(a => a.Name == "UnityRuntimeAnalysisAgent.Core").LoadedByAgent);

        var filtered = CodeAssembliesResult.Read(Call(Methods.CodeAssemblies, "{\"include\":[\"UnityRuntimeAnalysisAgent.TestAssemblies*\"],\"exclude\":[\"*.Broken\"]}"), "result").Items;
        Assert.Equal([Zoo], filtered.Select(a => a.Name).Distinct());

        var one = CodeAssemblyResult.Read(Call(Methods.CodeAssembly, $"{{\"mvid\":\"{ZooMvid}\"}}"), "result");
        Assert.Equal(Zoo, CodeAssemblyResult.Read(Call(Methods.CodeAssembly, $"{{\"name\":\"{Zoo}\"}}"), "result").Assembly.Name);
        Assert.Equal(zoo.Mvid, Assert.Single(one.Modules).Mvid);
        Assert.Contains(one.Attributes, a => a.Type == "System.Reflection.AssemblyTitleAttribute");
        Assert.Equal(ErrorCodes.NotFound, _peer.Call(Methods.CodeAssembly, "{\"name\":\"Nope\"}").Error!.Code);
    }

    [Fact]
    public void Types_filter_page_and_describe()
    {
        var shapes = CodeTypesResult.Read(Call(Methods.CodeTypes, $"{{\"assembly\":\"{Zoo}\",\"namespace\":\"Zoo.Il\",\"limit\":2}}"), "result");
        Assert.Equal(2, shapes.Items.Count);
        var rest = CodeTypesResult.Read(Call(Methods.CodeTypes, $"{{\"cursor\":\"{shapes.Cursor}\",\"limit\":100}}"), "result");
        Assert.Null(rest.Cursor);
        Assert.Equal(shapes.Total, shapes.Items.Count + rest.Items.Count);
        // IShape, Shape, Square, Circle, Corpus and the compiler's closure/state-machine types
        Assert.Equal(9, shapes.Items.Concat(rest.Items).Count(t => t.Anchor.Mvid == ZooMvid));

        var derived = CodeTypesResult.Read(Call(Methods.CodeTypes, $"{{\"baseType\":{Anchor(typeof(Shape))}}}"), "result");
        Assert.Equal(["Zoo.Il.Square"], derived.Items.Select(t => t.FullName));
        var implementers = CodeTypesResult.Read(Call(Methods.CodeTypes, $"{{\"implements\":{Anchor(typeof(IShape))},\"kind\":\"class\"}}"), "result");
        Assert.Equal(["Zoo.Il.Shape", "Zoo.Il.Square", "Zoo.Il.Circle"], implementers.Items.Where(t => t.Anchor.Mvid == ZooMvid).Select(t => t.FullName));
        var components = CodeTypesResult.Read(Call(Methods.CodeTypes, $"{{\"assembly\":\"{Zoo}\",\"isUnityComponent\":true,\"nameRegex\":\"Survey\"}}"), "result");
        Assert.Equal(["Zoo.Survey.Enemy", "Zoo.Survey.GameManager", "Zoo.Survey.Custom"], components.Items.Where(t => t.Anchor.Mvid == ZooMvid).Select(t => t.FullName));
        var marked = CodeTypesResult.Read(Call(Methods.CodeTypes, "{\"hasAttribute\":\"Zoo.Survey.FixtureSerializedAttribute\"}"), "result");
        Assert.Equal("Zoo.Survey.Custom", Assert.Single(marked.Items, t => t.Anchor.Mvid == ZooMvid).FullName);

        var square = CodeTypeResult.Read(Call(Methods.CodeType, $"{{\"type\":{Anchor(typeof(Square))}}}"), "result");
        Assert.Equal(["Zoo.Il.Shape", "System.Object"], square.BaseChain.Select(b => b.Name));
        Assert.Contains(square.Members, m => m.Name == "Area" && m.Kind == "method" && m.Visibility == "public" && m.Signature == "System.Double Area()");
        Assert.DoesNotContain(square.Members, m => m.Name == "Describe" && m.Anchor.Name!.StartsWith("Zoo.Il.Shape", StringComparison.Ordinal));
        var inherited = CodeTypeResult.Read(Call(Methods.CodeType, $"{{\"type\":{Anchor(typeof(Square))},\"inherited\":true}}"), "result");
        Assert.Contains(inherited.Members, m => m.Anchor.Name == "Zoo.Il.Shape::Describe()");
        var box = CodeTypeResult.Read(Call(Methods.CodeType, $"{{\"type\":{Anchor(typeof(Box<>))}}}"), "result");
        Assert.Equal("T", Assert.Single(box.GenericParameters!).Name);

        Assert.Equal(ErrorCodes.InvalidParams, _peer.Call(Methods.CodeType, $"{{\"type\":{Anchor(typeof(Plain).GetField("number")!)}}}").Error!.Code);
        Assert.Equal(ErrorCodes.IndexStale, _peer.Call(Methods.CodeType, $"{{\"type\":{{\"mvid\":\"{Guid.NewGuid()}\",\"token\":33554433}}}}").Error!.Code);
    }

    [Fact]
    public void Members_in_detail()
    {
        var limit = CodeMemberResult.Read(Call(Methods.CodeMember, $"{{\"member\":{Anchor(typeof(Services).GetField("Limit")!)}}}"), "result");
        Assert.Equal("3", limit.ConstantValue!.ToString());
        Assert.Equal("System.Int32", limit.FieldType);

        var health = CodeMemberResult.Read(Call(Methods.CodeMember, $"{{\"member\":{Anchor(typeof(Enemy).GetField("health")!)}}}"), "result");
        Assert.Equal("serialized", health.Serialized);
        var map = CodeMemberResult.Read(Call(Methods.CodeMember, $"{{\"member\":{Anchor(typeof(Enemy).GetField("map")!)}}}"), "result");
        Assert.Equal("not_serializable_type", map.Serialized);

        var describe = CodeMemberResult.Read(Call(Methods.CodeMember, $"{{\"member\":{Anchor(typeof(Square).GetMethod("Describe")!)}}}"), "result");
        Assert.Equal("Zoo.Il.Shape::Describe()", describe.BaseDefinition!.Name);
        Assert.True(describe.IsVirtual);
        Assert.True(describe.IlSize > 0);
        Assert.Equal("System.String", describe.ReturnType);

        var ctor = CodeMemberResult.Read(Call(Methods.CodeMember, $"{{\"member\":{Anchor(typeof(FixtureSerializedAttribute).GetConstructors()[0])}}}"), "result");
        var format = Assert.Single(ctor.Parameters!);
        Assert.True(format.HasDefault);
        Assert.Equal("\"binary\"", format.DefaultValue!.ToString());

        var exceptions = CodeMemberResult.Read(Call(Methods.CodeMember, $"{{\"member\":{Anchor(typeof(Corpus).GetMethod(nameof(Corpus.Exceptions))!)}}}"), "result");
        Assert.Equal(3, exceptions.ExceptionClauses);

        var property = CodeMemberResult.Read(Call(Methods.CodeMember, $"{{\"member\":{Anchor(typeof(Plain).GetProperty("Auto")!)}}}"), "result");
        Assert.Equal("Zoo.Plain::get_Auto()", property.Getter!.Name);
        var ev = CodeMemberResult.Read(Call(Methods.CodeMember, $"{{\"member\":{Anchor(typeof(WithEvents).GetEvent("Changed")!)}}}"), "result");
        Assert.Equal("Zoo.WithEvents::Changed", ev.BackingField!.Name);
        Assert.Equal("Zoo.WithEvents::add_Changed(System.EventHandler)", ev.Adder!.Name);

        var custom = CodeMemberResult.Read(Call(Methods.CodeMember, $"{{\"member\":{Anchor(typeof(Custom))}}}"), "result");
        var attribute = custom.Attributes!.Single(a => a.Type == "Zoo.Survey.FixtureSerializedAttribute");
        Assert.Equal("[\"json\"]", new JsonArray(attribute.CtorArgs!).ToString());
        Assert.Equal("{\"Version\":2}", attribute.NamedArgs!.ToString());
    }

    [Fact]
    public void Hierarchy_implementations_and_attributes()
    {
        var up = CodeHierarchyResult.Read(Call(Methods.CodeHierarchy, $"{{\"type\":{Anchor(typeof(Square))},\"direction\":\"up\",\"depth\":5}}"), "result");
        Assert.Equal(["Zoo.Il.Shape", "System.Object"], up.Items.Select(n => n.Type.Name));
        var down = CodeHierarchyResult.Read(Call(Methods.CodeHierarchy, $"{{\"type\":{Anchor(typeof(UnityEngine.MonoBehaviour))},\"direction\":\"down\"}}"), "result");
        Assert.Contains(down.Items, n => n.Type.Name == "Zoo.Survey.Enemy" && n.Depth == 1);

        var overrides = CodeImplementationsResult.Read(Call(Methods.CodeImplementations, $"{{\"method\":{Anchor(typeof(Shape).GetMethod("Describe")!)}}}"), "result");
        Assert.Equal("Zoo.Il.Square::Describe()", Assert.Single(overrides.Items).Method.Name);
        Assert.Equal("override", overrides.Items[0].Via);
        var implementations = CodeImplementationsResult.Read(Call(Methods.CodeImplementations, $"{{\"method\":{Anchor(typeof(IShape).GetMethod("Area")!)}}}"), "result");
        Assert.Equal(["Zoo.Il.Shape::Area()", "Zoo.Il.Square::Area()", "Zoo.Il.Circle::Area()"], implementations.Items.Select(i => i.Method.Name)); // Square through its base
        Assert.All(implementations.Items, i => Assert.Equal("interface", i.Via));

        var flags = CodeAttributesResult.Read(Call(Methods.CodeAttributes, "{\"attribute\":\"System.FlagsAttribute\",\"targets\":[\"type\"]}"), "result");
        Assert.Contains(flags.Items, u => u.Target.Name == "Zoo.Access");
        var marker = CodeAttributesResult.Read(Call(Methods.CodeAttributes, "{\"attribute\":\"Zoo.Survey.FixtureSerializedAttribute\"}"), "result");
        Assert.Equal(["type", "field"], marker.Items.Where(u => u.Target.Mvid == ZooMvid).Select(u => u.TargetKind));
    }

    [Fact]
    public void Il_and_hashes()
    {
        var method = typeof(Corpus).GetMethod(nameof(Corpus.Strings))!;
        var il = CodeIlResult.Read(Call(Methods.CodeIl, $"{{\"method\":{Anchor(method)}}}"), "result");
        Assert.Equal(method.GetMethodBody()!.GetILAsByteArray()!.Length, il.IlSize);
        Assert.Contains(il.Instructions!, i => i.Opcode == "ldstr" && i.Operand!.ToString() == "{\"string\":\"item_added\"}");
        var raw = CodeIlResult.Read(Call(Methods.CodeIl, $"{{\"method\":{Anchor(method)},\"resolveOperands\":false}}"), "result");
        Assert.Contains(raw.Instructions!, i => i.Opcode == "ldstr" && ((JsonObject)i.Operand!).ContainsKey("token"));
        var text = CodeIlResult.Read(Call(Methods.CodeIl, $"{{\"method\":{Anchor(method)},\"format\":\"text\"}}"), "result");
        Assert.Contains("ldstr \"item_removed\"", text.Text);
        var exceptions = CodeIlResult.Read(Call(Methods.CodeIl, $"{{\"method\":{Anchor(typeof(Corpus).GetMethod(nameof(Corpus.Exceptions))!)}}}"), "result");
        Assert.Equal(["filter", "catch", "finally"], exceptions.ExceptionClauses!.Select(c => c.Kind).OrderBy(k => k == "finally").ThenBy(k => k == "catch"));
        var abstractArea = CodeIlResult.Read(Call(Methods.CodeIl, $"{{\"method\":{Anchor(typeof(Shape).GetMethod("Area")!)}}}"), "result");
        Assert.Equal(0, abstractArea.IlSize);

        var hashes = CodeIlHashesResult.Read(Call(Methods.CodeIlHashes, $"{{\"methods\":[{Anchor(method)},{Anchor(typeof(Shape).GetMethod("Area")!)}]}}"), "result");
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(method.GetMethodBody()!.GetILAsByteArray()!)), hashes.Items[0].IlHashValue);
        Assert.Null(hashes.Items[1].IlHashValue);
        Assert.Equal(ErrorCodes.InvalidParams, _peer.Call(Methods.CodeIl, $"{{\"method\":{Anchor(typeof(Plain))}}}").Error!.Code);
    }

    [Fact]
    public void Cross_references()
    {
        var twice = typeof(Corpus).GetMethod(nameof(Corpus.Twice))!;
        var callers = CodeCallersResult.Read(Call(Methods.CodeCallers, $"{{\"method\":{Anchor(twice)},\"depth\":2}}"), "result");
        var names = callers.Items.Select(n => n.Method.Name!).ToList();
        Assert.Contains("Zoo.Il.Corpus::FunctionPointer(System.Int32)", names); // through ldftn
        Assert.Contains(names, n => n.Contains("<LaterAsync>d__", StringComparison.Ordinal)); // the async state machine's MoveNext
        Assert.NotEmpty(callers.Limits!);

        var callees = CodeCalleesResult.Read(Call(Methods.CodeCallees, $"{{\"method\":{Anchor(typeof(Corpus).GetMethod(nameof(Corpus.Calls))!)},\"depth\":2}}"), "result");
        var called = callees.Items.Select(n => n.Method.Name!).ToList();
        Assert.Contains(called, n => n.StartsWith("System.Collections.Generic.List`1<System.Int32>::Add", StringComparison.Ordinal));
        Assert.Contains("Zoo.Il.IShape::Area()", called); // the declared target of an interface call
        Assert.Contains("Zoo.Il.Corpus::Tick()", called);
        Assert.Contains(callees.Limits!, l => l.Contains("array methods", StringComparison.Ordinal));
        var tick = callees.Items.First(n => n.Method.Name == "Zoo.Il.Corpus::Tick()");
        Assert.Equal("ldftn", tick.Opcode);

        var fieldAccess = CodeFieldAccessResult.Read(Call(Methods.CodeFieldAccess, $"{{\"field\":{Anchor(typeof(Corpus).GetField("counter")!)}}}"), "result");
        Assert.Contains(fieldAccess.Items, a => a.Access == "write" && a.Method.Name == "Zoo.Il.Corpus::Tick()");
        var writes = CodeFieldAccessResult.Read(Call(Methods.CodeFieldAccess, $"{{\"field\":{Anchor(typeof(Square).GetField("side")!)},\"access\":\"address\"}}"), "result");
        Assert.Equal("Zoo.Il.Corpus::Fields(Zoo.Il.Square)", Assert.Single(writes.Items, a => a.Method.Mvid == ZooMvid).Method.Name);

        var strings = CodeStringsResult.Read(Call(Methods.CodeStrings, "{\"regex\":\"^item_\"}"), "result");
        Assert.Equal(["item_added", "item_label", "item_removed"], strings.Items.Where(s => s.Method.Mvid == ZooMvid).Select(s => s.Literal).Distinct().OrderBy(s => s.Length).ThenBy(s => s));
        Assert.Single(CodeStringsResult.Read(Call(Methods.CodeStrings, "{\"literal\":\"item_added\"}"), "result").Items, s => s.Method.Mvid == ZooMvid);
        Assert.Equal(ErrorCodes.InvalidParams, _peer.Call(Methods.CodeStrings, "{\"regex\":\"(\"}").Error!.Code);

        var allocations = CodeAllocationsResult.Read(Call(Methods.CodeAllocations, $"{{\"type\":{Anchor(typeof(Box<>))}}}"), "result");
        Assert.Contains(allocations.Items, a => a.Opcode == "newobj" && a.Method.Name == "Zoo.Il.Corpus::Calls(System.Collections.Generic.List`1<System.Int32>,Zoo.Il.IShape,Zoo.Il.Shape)");
        var point = CodeAllocationsResult.Read(Call(Methods.CodeAllocations, $"{{\"type\":{Anchor(typeof(Point))}}}"), "result");
        Assert.Contains(point.Items, a => a.Opcode == "initobj");
    }

    [Fact]
    public void Types_that_cant_load_are_skipped_not_fatal()
    {
        var broken = CodeTypesResult.Read(Call(Methods.CodeTypes, "{\"assembly\":\"UnityRuntimeAnalysisAgent.TestAssemblies.Broken\"}"), "result");
        Assert.Contains(broken.Items, t => t.FullName == "Broken.Fine");
        Assert.DoesNotContain(broken.Items, t => t.FullName == "Broken.DerivesFromMissing");
        var summary = CodeAssembliesResult.Read(Call(Methods.CodeAssemblies, "{\"include\":[\"*.Broken\"]}"), "result").Items.Single();
        Assert.Contains("UnityRuntimeAnalysisAgent.TestAssemblies.Missing", summary.ReferencedAssemblies);
    }

    public void Dispose() => _test.Dispose();

    private JsonValue Call(string method, string parameters)
    {
        var response = _peer.Call(method, parameters, timeoutMs: 60_000);
        Assert.True(response.Error is null, $"{method}: {response.Error?.Code} {response.Error?.Message}");
        return response.Result!;
    }

    private static string Anchor(MemberInfo member) => AnchorWriter.ForMember(member).ToJson().ToString();
}
