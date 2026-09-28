using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;
using Zoo;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Data;

/// <summary>Handles (strong, LRU-capped, identity-mapped, liveness-checked), variables and paging cursors.</summary>
public sealed class HandleTests : IDisposable
{
    private readonly FakeUnityApi _unity = new();
    private readonly TestHost _test = new(configure: c => c.Set(AgentConfig.MaxHandlesKey, "100"));

    [Fact]
    public void The_same_object_keeps_its_handle_and_the_least_recently_used_are_evicted()
    {
        var table = new HandleTable(_unity, max: 3);
        object a = new(), b = new(), c = new(), d = new();
        var ha = table.Mint(a);
        Assert.Equal(ha, table.Mint(a));
        var hb = table.Mint(b);
        table.Mint(c);
        Assert.Same(a, table.Resolve(ha)); // a is now the most recently used
        table.Mint(d);

        Assert.Equal(3, table.Count);
        var evicted = Assert.Throws<ProtocolException>(() => table.Resolve(hb));
        Assert.Equal(ErrorCodes.HandleExpired, evicted.Code);
        Assert.Equal("System.Object", ((JsonString)((JsonObject)evicted.ErrorData!["last"]!)["type"]!).Value);
        Assert.Same(a, table.Resolve(ha));
        Assert.NotEqual(hb, table.Mint(b)); // a new handle once released
    }

    [Fact]
    public void Pinned_handles_are_never_evicted_and_release_reports_unknown_ones()
    {
        var table = new HandleTable(_unity, max: 2);
        var kept = new object();
        var h = table.Mint(kept);
        table.Pin(h);
        for (var i = 0; i < 10; i++)
        {
            table.Mint(new object());
        }

        Assert.Same(kept, table.Resolve(h));

        var unknown = new List<long>();
        Assert.Equal(1, table.Release([h, 9999], unknown));
        Assert.Equal([9999L], unknown);
        Assert.Equal(ErrorCodes.HandleExpired, Assert.Throws<ProtocolException>(() => table.Resolve(h)).Code);

        var pinned = table.Mint(kept);
        table.Pin(pinned);
        table.Mint(new object());
        Assert.Equal(1, table.ReleaseAll(includePinned: false));
        Assert.True(table.Contains(pinned));
        Assert.Equal(1, table.ReleaseAll(includePinned: true));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Destroyed_unity_objects_expire_and_descriptors_carry_unity_facts()
    {
        var table = new HandleTable(_unity, max: 100);
        var go = _unity.World.Create("Player");
        var h = table.Mint(go);
        var descriptor = table.Describe(h);
        Assert.Equal("Player", descriptor.Unity!.Name);
        Assert.Equal(go.GetInstanceID(), descriptor.Unity.InstanceId);
        Assert.False(descriptor.Unity.Destroyed);
        Assert.Null(table.Describe(table.Mint(new Plain())).Unity);

        UnityEngine.Object.Destroy(go);
        Assert.True(table.Describe(h).Unity!.Destroyed);
        var expired = Assert.Throws<ProtocolException>(() => table.Resolve(h));
        Assert.Equal(ErrorCodes.HandleExpired, expired.Code);
        Assert.Equal("Player", ((JsonString)((JsonObject)expired.ErrorData!["last"]!)["name"]!).Value);
    }

    [Fact]
    public void Concurrent_use_keeps_identity_and_the_cap()
    {
        var table = new HandleTable(_unity, max: 500);
        var shared = Enumerable.Range(0, 50).Select(_ => new object()).ToArray();
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<long, object>();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        // Dedicated threads, not the thread pool: other tests' agents run on the pool at the same time.
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            try
            {
                Work();
            }
            catch (Exception e)
            {
                failures.Enqueue(e);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Empty(failures);
        Assert.True(table.Count <= 500);

        void Work()
        {
            for (var i = 0; i < 2000; i++)
            {
                var value = i % 3 == 0 ? shared[i % shared.Length] : new object();
                var h = table.Mint(value);
                if (ReferenceEquals(value, shared[i % shared.Length]))
                {
                    Assert.Same(seen.GetOrAdd(h, value), value);
                }

                try
                {
                    Assert.Same(value, table.Resolve(h));
                }
                catch (ProtocolException e) when (e.Code == ErrorCodes.HandleExpired)
                {
                    // evicted by another thread in between: allowed
                }
            }
        }
    }

    [Fact]
    public void Variables_hold_their_handles()
    {
        var table = new HandleTable(_unity, max: 2);
        var vars = new VariableStore(table);
        var held = new object();
        var h = table.Mint(held);
        Assert.False(vars.SetHandle("held", h));
        for (var i = 0; i < 10; i++)
        {
            table.Mint(new object());
        }

        Assert.Same(held, table.Resolve(vars.Get("held", "p").H));
        Assert.True(vars.SetValue("held", JsonValue.From(5))); // replacing unpins
        Assert.Equal(VariableKind.Value, vars.Get("held", "p").Kind);
        for (var i = 0; i < 10; i++)
        {
            table.Mint(new object());
        }

        Assert.False(table.Contains(h));
        Assert.True(vars.Delete("held"));
        Assert.False(vars.Delete("held"));
        Assert.Equal(ErrorCodes.NotFound, Assert.Throws<ProtocolException>(() => vars.Get("held", "params.name")).Code);
    }

    [Fact]
    public void Cursors_expire_after_ten_minutes()
    {
        var now = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        var cursors = new CursorStore(() => now);
        var cursor = cursors.Mint(41L);
        Assert.Equal(41L, cursors.Take<long>(cursor, "params.cursor"));
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => cursors.Take<string>(cursor, "params.cursor")).Code);

        now += TimeSpan.FromMinutes(10);
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => cursors.Take<long>(cursor, "params.cursor")).Code);
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => cursors.Take<long>("c-made-up", "params.cursor")).Code);
    }

    [Fact]
    public void Handles_are_listed_in_pages_and_released_over_the_wire()
    {
        var peer = _test.Connect();
        var data = _test.Host.Data;
        var objects = Enumerable.Range(0, 5).Select(_ => new Plain()).ToList();
        var handles = objects.Select(data.Handles.Mint).ToList();

        var first = HandlesListResult.Read(peer.Call(Methods.HandlesList, "{\"limit\":3}").Result, "result");
        Assert.Equal(handles.Take(3), first.Items.Select(i => i.H));
        Assert.Equal(5, first.Total);
        Assert.Equal("Zoo.Plain", first.Items[0].Type.Name);
        var second = HandlesListResult.Read(peer.Call(Methods.HandlesList, $"{{\"limit\":3,\"cursor\":\"{first.Cursor}\"}}").Result, "result");
        Assert.Equal(handles.Skip(3), second.Items.Select(i => i.H));
        Assert.Null(second.Cursor);

        var release = HandlesReleaseResult.Read(peer.Call(Methods.HandlesRelease, $"{{\"handles\":[{handles[0]},123456]}}").Result, "result");
        Assert.Equal(1, release.Released);
        Assert.Equal([123456L], release.Unknown);

        Assert.Null(peer.Call(Methods.VarsSet, $"{{\"name\":\"kept\",\"target\":{{\"h\":{handles[1]}}}}}").Error);
        Assert.Equal(3, HandlesReleaseAllResult.Read(peer.Call(Methods.HandlesReleaseAll).Result, "result").Released);
        Assert.True(data.Handles.Contains(handles[1])); // a variable holds it
        Assert.Equal(1, HandlesReleaseAllResult.Read(peer.Call(Methods.HandlesReleaseAll, "{\"includeVariables\":true}").Result, "result").Released);
        Assert.Empty(VarsListResult.Read(peer.Call(Methods.VarsList).Result, "result").Items);
    }

    [Fact]
    public void Variables_over_the_wire()
    {
        var peer = _test.Connect();
        var data = _test.Host.Data;
        var h = data.Handles.Mint(new Plain());

        var set = VarsSetResult.Read(peer.Call(Methods.VarsSet, $"{{\"name\":\"p\",\"target\":{{\"h\":{h}}}}}").Result, "result");
        Assert.Equal("handle", set.Variable.Kind);
        Assert.Equal(h, set.Variable.Descriptor!.H);
        Assert.False(set.Replaced);

        var plainAnchor = AnchorWriter.ForType(typeof(Plain)).ToJson();
        var statics = VarsSetResult.Read(peer.Call(Methods.VarsSet, $"{{\"name\":\"s\",\"target\":{{\"static\":{plainAnchor}}}}}").Result, "result");
        Assert.Equal("static", statics.Variable.Kind);
        Assert.Equal("Zoo.Plain", statics.Variable.Static!.Name);

        var copy = VarsSetResult.Read(peer.Call(Methods.VarsSet, "{\"name\":\"q\",\"target\":{\"var\":\"p\"}}").Result, "result");
        Assert.Equal(h, copy.Variable.Descriptor!.H);

        var value = VarsSetResult.Read(peer.Call(Methods.VarsSet, "{\"name\":\"p\",\"value\":{\"n\":[1,2]}}").Result, "result");
        Assert.True(value.Replaced);
        Assert.Equal("{\"n\":[1,2]}", value.Variable.Value!.ToString());

        _test.Unity.World.Create("Root").AddComponent<Example.Inventory>();
        var go = VarsSetResult.Read(_test.CallStepping(peer, Methods.VarsSet, "{\"name\":\"root\",\"target\":{\"gameObject\":{\"path\":\"Root\",\"scene\":\"Main\"}}}").Result, "result");
        Assert.Equal("Root", go.Variable.Descriptor!.Unity!.Name);
        Assert.Equal(ErrorCodes.NotFound, _test.CallStepping(peer, Methods.VarsSet, "{\"name\":\"x\",\"target\":{\"gameObject\":{\"path\":\"Nope\"}}}").Error!.Code);

        Assert.Equal(["p", "q", "root", "s"], VarsListResult.Read(peer.Call(Methods.VarsList).Result, "result").Items.Select(v => v.Name));
        Assert.Equal("value", VarsGetResult.Read(peer.Call(Methods.VarsGet, "{\"name\":\"p\"}").Result, "result").Variable.Kind);
        Assert.Equal(ErrorCodes.NotFound, peer.Call(Methods.VarsGet, "{\"name\":\"missing\"}").Error!.Code);
        Assert.Equal(ErrorCodes.InvalidParams, peer.Call(Methods.VarsSet, $"{{\"name\":\"both\",\"value\":1,\"target\":{{\"h\":{h}}}}}").Error!.Code);
        Assert.Equal(ErrorCodes.HandleExpired, peer.Call(Methods.VarsSet, "{\"name\":\"gone\",\"target\":{\"h\":987654}}").Error!.Code);
        Assert.True(VarsDeleteResult.Read(peer.Call(Methods.VarsDelete, "{\"name\":\"q\"}").Result, "result").Deleted);
    }

    public void Dispose() => _test.Dispose();
}
