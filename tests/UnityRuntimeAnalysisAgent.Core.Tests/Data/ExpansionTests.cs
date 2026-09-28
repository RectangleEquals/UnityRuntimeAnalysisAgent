using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;
using Zoo;
using Target = UnityLudometry.Protocol.Messages.Target;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Data;

/// <summary><c>value.expand</c> (deeper, a page of a list, a whole string), <c>REF_EXPIRED</c> → the locator,
/// <c>locator.resolve</c> for every locator kind, and <c>code.resolve</c>.</summary>
public sealed class ExpansionTests : IDisposable
{
    private readonly TestHost _test = new();

    private DataModel Data => _test.Host.Data;

    [Fact]
    public void Expanding_goes_deeper_pages_ranges_and_returns_whole_strings()
    {
        var peer = _test.Connect();
        var chain = Chain.Of(6);
        var root = new Target { H = Data.Handles.Mint(chain) };
        var shallow = (JsonObject)Data.Writer(new ViewOptions { Depth = 1 }, 1).Write(chain, new Place { Root = root });
        var next = RefOf(((JsonObject)shallow["fields"]!)["next"]!);

        var deeper = Expand(peer, $"{{\"ref\":\"{next}\",\"view\":{{\"depth\":8}}}}");
        var levels = 0;
        for (var node = (JsonObject)deeper.Value; node is not null; node = ((JsonObject)node["fields"]!)["next"] as JsonObject)
        {
            levels++;
        }

        Assert.Equal(5, levels); // levels 1..5, no stubs left
        Assert.True(deeper.Frame > 0);

        var everything = new Everything { longText = new string('x', 5000) };
        var target = new Target { H = Data.Handles.Mint(everything) };
        var names = (JsonObject)Data.Writer(new ViewOptions { MaxItems = 2 }, 1).Write(everything.names, new Place { Root = target }.Then(Step(typeof(Everything), "names"), ".names"));
        var tail = RefOf(((JsonArray)names["items"]!)[2]);
        var page = (JsonObject)Expand(peer, $"{{\"ref\":\"{tail}\"}}").Value;
        Assert.Equal("[\"c\",\"d\",\"e\"]", page["items"]!.ToString());
        Assert.Equal("2", ((JsonNumber)page["from"]!).RawText);

        var one = (JsonObject)Expand(peer, $"{{\"ref\":\"{tail}\",\"range\":[3,4]}}").Value;
        var items = (JsonArray)one["items"]!;
        Assert.Equal("d", ((JsonString)items[0]).Value);
        Assert.Equal("[4,5]", ((JsonObject)((JsonObject)items[1])["redacted"]!)["range"]!.ToString());

        var text = (JsonObject)Data.Writer(new ViewOptions { MaxString = 10 }, 1).Write(everything.longText, new Place { Root = target }.Then(Step(typeof(Everything), "longText"), ".longText"));
        Assert.Equal(everything.longText, ((JsonString)Expand(peer, $"{{\"ref\":\"{RefOf(text)}\"}}").Value).Value);

        Assert.Equal(ErrorCodes.InvalidParams, _test.CallStepping(peer, Methods.ValueExpand, $"{{\"ref\":\"{tail}\",\"range\":[4,3]}}").Error!.Code);
        var unknown = _test.CallStepping(peer, Methods.ValueExpand, "{\"ref\":\"x0:1\"}").Error!;
        Assert.Equal(ErrorCodes.RefExpired, unknown.Code);
    }

    [Fact]
    public void An_expired_ref_points_to_its_locator_which_still_resolves()
    {
        var peer = _test.Connect();
        var inventory = _test.Unity.World.Create("Player").AddComponent<Example.Inventory>();
        using var small = new DataModel(_test.Unity, maxHandles: 100, maxRefs: 1);
        var root = new Target { H = small.Handles.Mint(inventory) };
        var encoded = (JsonObject)small.Writer(new ViewOptions { Depth = 1, ExpandRootUnityObject = true }, 1).Write(inventory, new Place { Root = root, LocatorBase = small.Targets.LocatorBaseOf(inventory) });
        var stub = (JsonObject)((JsonObject)((JsonObject)encoded["fields"]!)["items"]!)["redacted"]!;
        Assert.Equal("live://Main/Player#Example.Inventory.items", ((JsonString)stub["locator"]!).Value);

        small.Expansions.Mint(new ExpansionEntry()); // evicts the stub's ref
        var expired = Assert.Throws<ProtocolException>(() => small.Expansions.Get(RefOf(((JsonObject)encoded["fields"]!)["items"]!)));
        Assert.Equal(ErrorCodes.RefExpired, expired.Code);
        var locator = ((JsonString)expired.ErrorData!["locator"]!).Value;

        var resolved = LocatorResolveResult.Read(_test.CallStepping(peer, Methods.LocatorResolve, $"{{\"locator\":\"{locator}\"}}").Result, "result");
        Assert.Equal("Example.Inventory", resolved.Descriptor!.Type.Name);
        Assert.Equal("Example.Inventory::items", Assert.Single(resolved.Path).Member!.Name);
        Assert.Same(inventory, Data.Handles.Resolve(resolved.Target.H!.Value));
    }

    [Fact]
    public void Every_locator_kind()
    {
        var peer = _test.Connect();
        var manager = _test.Unity.World.Create("Manager", scene: "ddol");
        manager.AddComponent<Example.Inventory>();

        var ddol = Resolve(peer, "live://ddol/Manager#Example.Inventory.items[1]");
        Assert.Equal(1, ddol.Path[1].Index);

        var gameObject = Resolve(peer, "live://ddol/Manager#UnityEngine.GameObject");
        Assert.Same(manager, Data.Handles.Resolve(gameObject.Target.H!.Value));
        Assert.Empty(gameObject.Path);

        var statics = Resolve(peer, "live://static/Zoo.Plain.StaticCount");
        Assert.Equal("Zoo.Plain", statics.Target.Static!.Name);
        Assert.Equal("Zoo.Plain::StaticCount", Assert.Single(statics.Path).Member!.Name);
        var nested = Resolve(peer, "live://static/Zoo.Plain.StaticAuto");
        Assert.Equal("Zoo.Plain::StaticAuto", Assert.Single(nested.Path).Member!.Name);

        var type = Resolve(peer, AnchorWriter.CodeLocator(typeof(Plain)));
        Assert.Equal("Zoo.Plain", type.Target.Static!.Name);
        Assert.Empty(type.Path);
        var method = typeof(Overloads).GetMethod("Do", [typeof(int)])!;
        var member = Resolve(peer, AnchorWriter.CodeLocator(method) + "#IL_0001");
        Assert.Equal("Zoo.Overloads::Do(System.Int32)", Assert.Single(member.Path).Member!.Name);

        Assert.Equal(ErrorCodes.Unsupported, Fails(peer, "live://asset/Zoo.Settings/Default#-5.volume"));
        Assert.Equal(ErrorCodes.Unsupported, Fails(peer, "addr://ui/main"));
        Assert.Equal(ErrorCodes.NotFound, Fails(peer, "live://Main/Nobody#UnityEngine.GameObject"));
        Assert.Equal(ErrorCodes.NotFound, Fails(peer, "live://ddol/Manager#Zoo.Player"));
        Assert.Equal(ErrorCodes.NotFound, Fails(peer, "live://static/No.Such.Type"));
        Assert.Equal(ErrorCodes.InvalidParams, Fails(peer, "live://Main/Player"));
        Assert.Equal(ErrorCodes.InvalidParams, Fails(peer, "code://Zoo@not-a-guid/1"));
        Assert.Equal(ErrorCodes.IndexStale, Fails(peer, $"code://Zoo@{Guid.NewGuid()}/33554435"));
    }

    [Fact]
    public void Code_resolve_is_exploratory()
    {
        // A name search covers every loaded build (other tests may load copies of the zoo): count the zoo's own build.
        var peer = _test.Connect();
        var zoo = typeof(Plain).Module.ModuleVersionId.ToString();
        List<Anchor> Candidates(string parameters) =>
            CodeResolveResult.Read(peer.Call(Methods.CodeResolve, parameters).Result, "result").Candidates.Where(c => c.Mvid == zoo).ToList();

        Assert.True(CodeResolveResult.Read(peer.Call(Methods.CodeResolve, "{\"type\":\"Zoo.Plain\"}").Result, "result").Exploratory);
        Assert.Equal("Zoo.Plain", Assert.Single(Candidates("{\"type\":\"Zoo.Plain\"}")).Name);
        Assert.Equal(4, Candidates("{\"type\":\"Zoo.Overloads\",\"member\":\"Do\"}").Count);
        Assert.Equal("Zoo.Overloads::Do(System.Int32)", Assert.Single(Candidates("{\"type\":\"Zoo.Overloads\",\"member\":\"Do\",\"signature\":\"Void Do(Int32)\"}")).Name);
        Assert.Single(Candidates("{\"type\":\"Zoo.Outer/Inner\",\"assembly\":\"UnityRuntimeAnalysisAgent.TestAssemblies\"}"));
        Assert.Empty(Candidates("{\"type\":\"Zoo.Plain\",\"assembly\":\"Other\"}"));
    }

    public void Dispose() => _test.Dispose();

    private ValueExpandResult Expand(WirePeer peer, string parameters)
    {
        var response = _test.CallStepping(peer, Methods.ValueExpand, parameters);
        Assert.Null(response.Error);
        return ValueExpandResult.Read(response.Result, "result");
    }

    private LocatorResolveResult Resolve(WirePeer peer, string locator)
    {
        var response = _test.CallStepping(peer, Methods.LocatorResolve, $"{{\"locator\":\"{locator}\"}}");
        Assert.True(response.Error is null, response.Error?.Message);
        return LocatorResolveResult.Read(response.Result, "result");
    }

    private string Fails(WirePeer peer, string locator) => _test.CallStepping(peer, Methods.LocatorResolve, $"{{\"locator\":\"{locator}\"}}").Error!.Code;

    private static string RefOf(JsonValue stub) => ((JsonString)((JsonObject)((JsonObject)stub)["redacted"]!)["ref"]!).Value;

    private static MemberPathStep Step(Type type, string field) => new() { Member = AnchorWriter.ForMember(type.GetField(field)!) };
}
