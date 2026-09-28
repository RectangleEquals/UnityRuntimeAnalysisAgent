using System.Text.Json;
using System.Text.RegularExpressions;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;
using Zoo;
using Target = UnityLudometry.Protocol.Messages.Target;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Data;

/// <summary>
/// The value codec: snapshots of the type zoo (every kind of value), a redaction stub for every limit (each with
/// a ref that re-reads it), and JSON → live conversions.
/// </summary>
public sealed partial class CodecTests : IDisposable
{
    private readonly FakeUnityApi _unity = new();
    private readonly DataModel _data;

    public CodecTests() => _data = new DataModel(_unity, maxHandles: 1000);

    [Fact]
    public Task Everything_with_the_default_view()
    {
        var everything = new Everything { owner = _unity.World.Create("Owner") };
        everything.onUse.AddPersistent(everything.owner, "Use");
        everything.onUse.AddRuntimeListener(new object());
        return Snapshot(Write(everything, new Target { H = _data.Handles.Mint(everything) }));
    }

    [Fact]
    public Task Properties_anchors_and_a_throwing_getter()
    {
        var plain = new Plain();
        return Snapshot(Write(plain, new Target { H = _data.Handles.Mint(plain) }, new ViewOptions { Properties = true, Anchors = true }));
    }

    [Fact]
    public Task A_unity_object_is_a_descriptor_unless_it_is_the_root_being_inspected()
    {
        var go = _unity.World.Create("Hero");
        var player = go.AddComponent<Player>();
        var target = new Target { H = _data.Handles.Mint(player) };
        var described = Write(player, target);
        var inspected = Write(player, target, new ViewOptions { ExpandRootUnityObject = true });
        return Snapshot(new JsonObject { { "described", described }, { "inspected", inspected } });
    }

    [Fact]
    public void Every_limit_leaves_an_expandable_stub()
    {
        var everything = new Everything();
        var root = new Target { H = _data.Handles.Mint(everything) };
        var tight = new ViewOptions { Depth = 1, MaxItems = 2, MaxString = 10 };
        var writer = _data.Writer(tight, frame: 7);
        var value = (JsonObject)writer.Write(everything, Place(root));
        var fields = (JsonObject)value["fields"]!;

        // depth: containers one level down are stubs
        var depth = Stub(fields["plain"]);
        Assert.Equal("depth", Text(depth["reason"]));
        Assert.Equal("Zoo.Plain", Text(((JsonObject)depth["type"]!)["name"]));
        Assert.Equal("plain", Text(depth["member"]));
        Assert.Equal("Zoo.Everything::plain", Text(((JsonObject)((JsonObject)((JsonArray)depth["path"]!)[0])["member"]!)["name"]));
        Assert.Equal(_data.Handles.Mint(everything.plain), ((JsonNumber)depth["h"]!).TryGetInt64(out var h) ? h : 0);
        Assert.Equal(everything.plain, Expand(depth).Value);

        var list = Stub(fields["names"]);
        Assert.Equal("depth", Text(list["reason"]));
        Assert.Equal("5", ((JsonNumber)((JsonObject)list["size"]!)["count"]!).RawText);

        // maxString: the prefix and the full length
        var text = Stub(fields["longText"]);
        Assert.Equal("maxString", Text(text["reason"]));
        Assert.Equal("xxxxxxxxxx", Text(text["preview"]));
        Assert.Equal("40", ((JsonNumber)((JsonObject)text["size"]!)["length"]!).RawText);
        Assert.Equal(everything.longText, Expand(text).Value);

        // notEnumerated: lazy sequences aren't run unless asked
        Assert.Equal("notEnumerated", Text(Stub(fields["lazy"])["reason"]));

        // maxItems: a range stub for the tail, one level deeper
        var names = (JsonObject)_data.Writer(new ViewOptions { MaxItems = 2 }, 7).Write(everything.names, Place(root, "names"));
        var items = (JsonArray)names["items"]!;
        Assert.Equal(3, items.Count);
        var tail = Stub(items[2]);
        Assert.Equal("maxItems", Text(tail["reason"]));
        Assert.Equal("[2,5]", tail["range"]!.ToString());
        Assert.Equal((2, 5), _data.Expansions.Get(Text(tail["ref"])).Range);

        // maxMembers: one stub stands for the members left out
        var wide = new Wide();
        var more = Stub(((JsonObject)((JsonObject)_data.Writer(new ViewOptions { MaxMembers = 5 }, 7).Write(wide, Place(new Target { H = _data.Handles.Mint(wide) })))["fields"]!)["$more"]);
        Assert.Equal("maxMembers", Text(more["reason"]));
        Assert.Equal("70", ((JsonNumber)((JsonObject)more["size"]!)["count"]!).RawText);

        Assert.Equal(["depth", "maxString", "notEnumerated"], writer.Redactions.Keys.Where(k => k is "depth" or "maxString" or "notEnumerated"));
        foreach (var stub in new[] { depth, list, text, tail, more })
        {
            Assert.StartsWith("x", Text(stub["ref"])); // schema validity: the protocol conformance tests
        }
    }

    [Fact]
    public void Values_without_a_path_are_retained_and_policy_stubs_have_no_ref()
    {
        // A struct key's value can't be addressed by a path: its ref keeps the value itself.
        var writer = _data.Writer(new ViewOptions { Depth = 1 }, 1);
        var byPoint = new Dictionary<Point, Plain> { [new Point(1, 1)] = new Plain() };
        var encoded = (JsonObject)writer.Write(byPoint, new Place());
        var entry = (JsonObject)((JsonArray)encoded["entries"]!)[0]!;
        var stub = Stub(entry["v"]);
        var expansion = _data.Expansions.Get(Text(stub["ref"]));
        Assert.Equal(new Target { H = _data.Handles.Mint(byPoint[new Point(1, 1)]) }.H, expansion.Root!.H); // re-rooted at its handle

        var retained = Stub(writer.Write("0123456789".PadRight(2000, '!'), new Place()));
        Assert.True(_data.Expansions.Get(Text(retained["ref"])).HasRetained);

        var policy = writer.Stub("policy", "secret", new Place());
        Assert.False(((JsonObject)policy["redacted"]!).ContainsKey("ref"));
    }

    [Fact]
    public void Cycles_and_shared_objects_are_written_once()
    {
        var everything = new Everything();
        var fields = (JsonObject)((JsonObject)_data.Writer(new ViewOptions { Depth = 3 }, 1).Write(everything, new Place()))["fields"]!;
        var self = (JsonObject)fields["self"]!;
        Assert.Equal(_data.Handles.Mint(everything), ((JsonNumber)self["ref"]!).TryGetInt64(out var h) ? h : 0);
        Assert.Equal("object", Text(((JsonObject)fields["plain"]!)["t"]));
        Assert.True(((JsonObject)fields["shared"]!).ContainsKey("ref"));
    }

    [Fact]
    public void Enumerate_runs_lazy_sequences_and_safe_mode_runs_no_getters()
    {
        var everything = new Everything();
        var lazy = (JsonObject)_data.Writer(new ViewOptions { Enumerate = true }, 1).Write(everything.lazy, new Place());
        Assert.Equal("[1,2]", lazy["items"]!.ToString());

        var plain = new Plain();
        var safe = (JsonObject)_data.Writer(new ViewOptions { Properties = true, Safe = true }, 1).Write(plain, new Place());
        Assert.False(safe.ContainsKey("props"));
    }

    [Fact]
    public void Json_to_live_conversions()
    {
        var reader = _data.Reader;
        Assert.Equal(42, reader.Read(JsonValue.From(42), typeof(int), "p"));
        Assert.Equal(long.MinValue, reader.Read(JsonValue.Parse("{\"t\":\"i64\",\"v\":\"-9223372036854775808\"}"), typeof(long), "p"));
        Assert.Equal(ulong.MaxValue, reader.Read(JsonValue.Parse("{\"t\":\"u64\",\"v\":\"18446744073709551615\"}"), typeof(ulong), "p"));
        Assert.True(float.IsNaN((float)reader.Read(JsonValue.Parse("{\"t\":\"f32\",\"v\":\"NaN\"}"), typeof(float), "p")!));
        Assert.Equal(0.5, reader.Read(JsonValue.From(0.5), typeof(double), "p"));
        Assert.Equal(12.34m, reader.Read(JsonValue.Parse("12.34"), typeof(decimal), "p"));
        Assert.Equal(Access.Read | Access.Exec, reader.Read(JsonValue.From("Read, Exec"), typeof(Access), "p"));
        Assert.Equal(Access.Write, reader.Read(JsonValue.From(2), typeof(Access), "p"));
        Assert.Equal('Q', reader.Read(JsonValue.From("Q"), typeof(char), "p"));
        Assert.Equal(new Guid("5b0e1c7a-3f2d-4e8b-9a61-0c2d4e6f8a1b"), reader.Read(JsonValue.From("5b0e1c7a-3f2d-4e8b-9a61-0c2d4e6f8a1b"), typeof(Guid), "p"));
        Assert.Equal(new TimeSpan(1, 2, 3, 4), reader.Read(JsonValue.Parse("{\"t\":\"timespan\",\"v\":\"1.02:03:04\"}"), typeof(TimeSpan), "p"));
        Assert.Null(reader.Read(JsonNull.Instance, typeof(int?), "p"));
        Assert.Equal(typeof(Plain), reader.Read(AnchorWriter.ForType(typeof(Plain)).ToJson(), typeof(Type), "p"));
        Assert.Equal(typeof(int), reader.Read(JsonValue.From("System.Int32"), typeof(Type), "p"));

        var plain = new Plain();
        Assert.Same(plain, reader.Read(JsonValue.Parse($"{{\"h\":{_data.Handles.Mint(plain)}}}"), typeof(Plain), "p"));
        _data.Variables.SetValue("five", JsonValue.From(5));
        Assert.Equal(5, reader.Read(JsonValue.Parse("{\"var\":\"five\"}"), typeof(int), "p"));

        var position = (UnityEngine.Vector3)reader.Read(JsonValue.Parse("{\"t\":\"Vector3\",\"x\":1.5,\"y\":0,\"z\":-2}"), typeof(UnityEngine.Vector3), "p")!;
        Assert.Equal((1.5f, 0f, -2f), (position.x, position.y, position.z));
        var rect = (UnityEngine.Rect)reader.Read(JsonValue.Parse("{\"t\":\"Rect\",\"x\":1,\"y\":2,\"width\":3,\"height\":4}"), typeof(UnityEngine.Rect), "p")!;
        Assert.Equal((1f, 2f, 3f, 4f), (rect.m_XMin, rect.m_YMin, rect.m_Width, rect.m_Height));

        Assert.Equal([1, 2, 3], (int[])reader.Read(JsonValue.Parse("[1,2,3]"), typeof(int[]), "p")!);
        Assert.Equal(["a", "b"], (List<string>)reader.Read(JsonValue.Parse("[\"a\",\"b\"]"), typeof(List<string>), "p")!);
        Assert.Equal([7], (HashSet<int>)reader.Read(JsonValue.Parse("[7]"), typeof(HashSet<int>), "p")!);
        var dict = (Dictionary<string, int>)reader.Read(JsonValue.Parse("{\"t\":\"dict\",\"entries\":[{\"k\":\"a\",\"v\":1}]}"), typeof(Dictionary<string, int>), "p")!;
        Assert.Equal(1, dict["a"]);

        var pointType = AnchorWriter.ForType(typeof(Point)).ToJson();
        var ctor = AnchorWriter.ForMember(typeof(Point).GetConstructor([typeof(int), typeof(int)])!).ToJson();
        Assert.Equal(new Point(3, 4), reader.Read(JsonValue.Parse($"{{\"t\":\"new\",\"type\":{pointType},\"ctor\":{ctor},\"args\":[3,4]}}"), typeof(Point), "p"));
        var built = (Plain)reader.Read(JsonValue.Parse($"{{\"t\":\"new\",\"type\":{AnchorWriter.ForType(typeof(Plain)).ToJson()},\"fields\":{{\"number\":5,\"Auto\":9}}}}"), typeof(Plain), "p")!;
        Assert.Equal((5, 9), (built.number, built.Auto));
        var bare = (Plain)reader.Read(JsonValue.Parse($"{{\"t\":\"new\",\"type\":{AnchorWriter.ForType(typeof(Plain)).ToJson()},\"uninitialized\":true}}"), typeof(Plain), "p")!;
        Assert.Equal(0, bare.number); // no constructor ran

        var tooBig = Assert.Throws<ProtocolException>(() => reader.Read(JsonValue.From(3_000_000_000), typeof(int), "params.value"));
        Assert.Equal(ErrorCodes.InvalidParams, tooBig.Code);
        Assert.Equal("params.value", Text(tooBig.ErrorData!["param"]));
        Assert.Equal("System.Int32", Text(tooBig.ErrorData["expectedType"]));
        Assert.Equal("3000000000", ((JsonNumber)tooBig.ErrorData["got"]!).RawText);
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => reader.Read(JsonNull.Instance, typeof(int), "p")).Code);
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => reader.Read(JsonValue.From("Nope"), typeof(Access), "p")).Code);
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => reader.Read(JsonValue.Parse($"{{\"h\":{_data.Handles.Mint(plain)}}}"), typeof(Point), "p")).Code);
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => reader.Read(JsonValue.Parse("{\"t\":\"Vector3\",\"x\":1}"), typeof(Plain), "p")).Code);
    }

    public void Dispose() => _data.Dispose();

    private JsonValue Write(object value, Target root, ViewOptions? view = null) => _data.Writer(view ?? new ViewOptions(), frame: 1).Write(value, Place(root));

    private static Place Place(Target root, string? field = null)
    {
        var place = new Place { Root = root };
        return field is null ? place : place.Then(new MemberPathStep { Name = field }, "." + field);
    }

    // What value.expand does with a ref: re-resolve its root and path.
    private Resolved Expand(JsonObject stub)
    {
        var entry = _data.Expansions.Get(Text(stub["ref"]));
        return entry.HasRetained ? new Resolved { Value = entry.Retained } : _data.Targets.Resolve(entry.Root!, entry.Path, "p", "p");
    }

    private static JsonObject Stub(JsonValue? value) => (JsonObject)((JsonObject)value!)["redacted"]!;

    private static string Text(JsonValue? value) => ((JsonString)value!).Value;

    // MVIDs, instance ids and ref run prefixes differ between builds and runs.
    private static Task Snapshot(JsonValue value)
    {
        var json = JsonSerializer.Serialize(JsonDocument.Parse(value.ToString()).RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        json = Mvid().Replace(json, "\"mvid\": \"{mvid}\"");
        json = InstanceId().Replace(json, "\"instanceId\": {id}");
        json = Ref().Replace(json, "\"x{run}:$1\"");
        return Verify(json, "json");
    }

    [GeneratedRegex("\"mvid\": \"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\"")]
    private static partial Regex Mvid();

    [GeneratedRegex("\"instanceId\": -?\\d+")]
    private static partial Regex InstanceId();

    [GeneratedRegex("\"x[0-9a-f]+:(\\d+)\"")]
    private static partial Regex Ref();
}
