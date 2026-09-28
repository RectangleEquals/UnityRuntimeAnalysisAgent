using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;
using Zoo;
using Target = UnityLudometry.Protocol.Messages.Target;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Data;

/// <summary>Targets and member paths: chains across fields, properties, lists, dictionaries, components and children,
/// exploratory names, and errors that name the failing step.</summary>
public sealed class TargetTests : IDisposable
{
    private readonly FakeUnityApi _unity = new();
    private readonly DataModel _data;

    public TargetTests() => _data = new DataModel(_unity, maxHandles: 1000);

    [Fact]
    public void Static_targets_read_static_members_by_anchor_or_name()
    {
        var target = new Target { Static = AnchorWriter.ForType(typeof(Plain)) };
        Assert.Equal(3, Resolve(target, Member(typeof(Plain), "StaticCount")).Value);

        var byName = Resolve(target, new MemberPathStep { Name = "StaticAuto" });
        Assert.Equal("static", byName.Value);
        Assert.Equal("EXPLORATORY_NAME", Assert.Single(byName.Warnings).Code);
        Assert.Equal("Zoo.Plain::StaticAuto", byName.Place.Path[0].Member!.Name); // the name became an anchor
        Assert.Equal("live://static/Zoo.Plain.StaticAuto", byName.Place.Locator);

        Assert.Equal(ErrorCodes.InvalidParams, Fails(target, Member(typeof(Plain), "number")).Code); // an instance member
    }

    [Fact]
    public void Chains_across_fields_properties_lists_and_dictionaries()
    {
        var everything = new Everything();
        var target = new Target { H = _data.Handles.Mint(everything) };

        Assert.Equal(42, Resolve(target, Member(typeof(Everything), "plain"), Member(typeof(Plain), "number")).Value);
        Assert.Equal(84, Resolve(target, Member(typeof(Everything), "plain"), Property(typeof(Plain), "Computed")).Value);
        Assert.Equal(7, Resolve(target, new MemberPathStep { Name = "plain" }, new MemberPathStep { Name = "Auto" }).Value);
        Assert.Equal(2, Resolve(target, Member(typeof(Everything), "array"), new MemberPathStep { Index = 1 }).Value);
        Assert.Equal("c", Resolve(target, Member(typeof(Everything), "names"), new MemberPathStep { Index = 2 }).Value);
        Assert.Equal(7, Resolve(target, Member(typeof(Everything), "set"), new MemberPathStep { Index = 0 }).Value);
        Assert.Equal(2, Resolve(target, Member(typeof(Everything), "scores"), new MemberPathStep { Key = JsonValue.From("y") }).Value);
        var byStructKey = Resolve(target, Member(typeof(Everything), "byPoint"),
            new MemberPathStep { Key = JsonValue.Parse($"{{\"t\":\"new\",\"type\":{AnchorWriter.ForType(typeof(Point)).ToJson()},\"fields\":{{\"X\":1,\"Y\":2}}}}") });
        Assert.Equal("p", byStructKey.Value);

        var chain = Resolve(new Target { H = _data.Handles.Mint(Chain.Of(5)) }, Enumerable.Repeat(new MemberPathStep { Name = "next" }, 3).Append(new MemberPathStep { Name = "level" }).ToArray());
        Assert.Equal(3, chain.Value);
    }

    [Fact]
    public void Generic_members_bind_to_the_runtime_type_and_errors_name_their_step()
    {
        var box = new Box<int> { item = 9 };
        var target = new Target { H = _data.Handles.Mint(box) };
        var definitionField = AnchorWriter.ForMember(typeof(Box<>).GetField("item")!);
        Assert.Equal(9, Resolve(target, new MemberPathStep { Member = definitionField }).Value);

        var everything = new Target { H = _data.Handles.Mint(new Everything { nothing = null }) };
        var outOfRange = Fails(everything, Member(typeof(Everything), "names"), new MemberPathStep { Index = 99 });
        Assert.Equal(ErrorCodes.NotFound, outOfRange.Code);
        Assert.Equal("params.path[1].index", ((JsonString)outOfRange.ErrorData!["param"]!).Value);
        Assert.Equal(ErrorCodes.NotFound, Fails(everything, Member(typeof(Everything), "scores"), new MemberPathStep { Key = JsonValue.From("zz") }).Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails(everything, Member(typeof(Everything), "flag"), new MemberPathStep { Index = 0 }).Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails(everything, Member(typeof(Everything), "flag"), new MemberPathStep { Key = JsonValue.From("a") }).Code);
        var afterNull = Fails(everything, Member(typeof(Everything), "nothing"), new MemberPathStep { Name = "Length" });
        Assert.Equal(ErrorCodes.NotFound, afterNull.Code);
        Assert.Equal("params.path[1]", ((JsonString)afterNull.ErrorData!["param"]!).Value);
        Assert.Equal(ErrorCodes.InvalidParams, Fails(everything, Member(typeof(Plain), "number")).Code); // not a member of Everything

        var throwing = Assert.ThrowsAny<Exception>(() => Resolve(new Target { H = _data.Handles.Mint(new Plain()) }, Property(typeof(Plain), "Throws")));
        Assert.Equal(ErrorCodes.GameException, UnityRuntimeAnalysisAgent.Core.Dispatch.AgentErrors.FromException(throwing, new TestLogger(), "test").Code);

        var ambiguous = Fails(new Target { H = _data.Handles.Mint(new Derived()) }, new MemberPathStep { Name = "shadowed" });
        Assert.Equal(ErrorCodes.Ambiguous, ambiguous.Code);
        Assert.Equal(2, ((JsonArray)ambiguous.ErrorData!["candidates"]!).Count);
        Assert.Equal(ErrorCodes.NotFound, Fails(everything, new MemberPathStep { Name = "noSuchMember" }).Code);
    }

    [Fact]
    public void GameObjects_components_and_children_with_their_locators()
    {
        var player = _unity.World.Create("Player");
        var inventory = player.AddComponent<Example.Inventory>();
        _unity.World.Create("Weapon", parent: _unity.World.Create("Hand", parent: player));

        var target = new Target { GameObject = new GameObjectRef { Path = "Player", Scene = "Main" } };
        var item = Resolve(target, new MemberPathStep { Component = AnchorWriter.ForType(typeof(Example.Inventory)) }, Member(typeof(Example.Inventory), "items"), new MemberPathStep { Index = 0 });
        Assert.Equal(1, item.Value);
        Assert.Equal("live://Main/Player#Example.Inventory.items[0]", item.Place.Locator);

        var weapon = Resolve(target, new MemberPathStep { Child = "Hand/Weapon" });
        Assert.Equal("Weapon", ((UnityEngine.GameObject)weapon.Value!).name);
        Assert.Equal("live://Main/Player/Hand/Weapon#UnityEngine.GameObject", weapon.Place.Locator);

        var viaHandle = Resolve(new Target { H = _data.Handles.Mint(inventory) }, Member(typeof(Example.Inventory), "items"));
        Assert.Equal("live://Main/Player#Example.Inventory.items", viaHandle.Place.Locator);

        Assert.Equal(ErrorCodes.NotFound, Fails(target, new MemberPathStep { Component = AnchorWriter.ForType(typeof(Player)) }).Code);
        Assert.Equal(ErrorCodes.NotFound, Fails(target, new MemberPathStep { Child = "Nope" }).Code);
        Assert.Equal(ErrorCodes.NotFound, Fails(new Target { GameObject = new GameObjectRef { Path = "Player", Scene = "Other" } }).Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails(new Target { H = _data.Handles.Mint(new Plain()) }, new MemberPathStep { Child = "x" }).Code);
    }

    [Fact]
    public void Variables_are_targets()
    {
        _data.Variables.SetStatic("plainStatics", AnchorWriter.ForType(typeof(Plain)));
        Assert.Equal(3, Resolve(new Target { Var = "plainStatics" }, new MemberPathStep { Name = "StaticCount" }).Value);
        _data.Variables.SetHandle("box", _data.Handles.Mint(new Box<string> { item = "x" }));
        Assert.Equal("x", Resolve(new Target { Var = "box" }, new MemberPathStep { Name = "item" }).Value);
        _data.Variables.SetValue("number", JsonValue.From(1));
        Assert.Equal(ErrorCodes.InvalidParams, Fails(new Target { Var = "number" }).Code);
        Assert.Equal(ErrorCodes.NotFound, Fails(new Target { Var = "missing" }).Code);
    }

    public void Dispose() => _data.Dispose();

    private Resolved Resolve(Target target, params MemberPathStep[] path) => _data.Targets.Resolve(target, path, "params.target", "params.path");

    private ProtocolException Fails(Target target, params MemberPathStep[] path) => Assert.Throws<ProtocolException>(() => Resolve(target, path));

    private static MemberPathStep Member(Type type, string field) =>
        new() { Member = AnchorWriter.ForMember(type.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)!) };

    private static MemberPathStep Property(Type type, string property) => new() { Member = AnchorWriter.ForMember(type.GetProperty(property)!) };
}
