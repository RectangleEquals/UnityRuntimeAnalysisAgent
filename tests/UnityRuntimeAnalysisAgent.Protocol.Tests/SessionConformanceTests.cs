using UnityRuntimeAnalysisAgent.TestAssemblies;
using System.Net;
using System.Net.Sockets;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Conformance;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Framing;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Protocol.Tests;

/// <summary>
/// Sends the pinned protocol's fixtures of every method this build implements (the session, modes, log level, batch, jobs,
/// the activity feed, the data model and code introspection) to a real in-process agent and checks its responses: same outcome and error code as the fixture,
/// results valid against the schemas, and the connection closed after a failed handshake. Events the agent emits are
/// validated against their schemas too.
/// </summary>
public sealed class SessionConformanceTests : IDisposable
{
    private static readonly string[] SessionGroups =
    [
        "hello", "ping", "agent.info", "agent.capabilities", "cancel", "events.subscribe", "events.unsubscribe", "_generic",
        "agent.setMode", "agent.logLevel", "batch", "job.get", "job.wait", "job.cancel", "job.list", "activity.list", "activity.get",
        "agent.selfTest",
        "handles.list", "handles.release", "handles.releaseAll", "vars.set", "vars.get", "vars.list", "vars.delete", "value.expand",
        "locator.resolve", "code.resolve",
        "code.assemblies", "code.assembly", "code.types", "code.type", "code.member", "code.hierarchy", "code.implementations", "code.attributes",
        "code.ilHashes", "code.il", "code.callers", "code.callees", "code.fieldAccess", "code.strings", "code.allocations", "il.index.start", "survey.start",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, FixtureCase>> Fixtures = new(() =>
        FixtureCatalog.Load(Path.Combine(ProtocolSchemas.Root, "fixtures", "agent")).Where(f => SessionGroups.Contains(f.Group)).ToDictionary(f => f.Id));

    private readonly string _providers = Path.Combine(Path.GetTempPath(), "uraa-tests", Guid.NewGuid().ToString("N"));
    private readonly AgentHost _host;

    public SessionConformanceTests()
    {
        var config = new DictionaryConfigSource().Set(AgentConfig.TransportModeKey, "tcp").Set(AgentConfig.ProvidersDirKey, _providers);
        _host = new AgentHost(config, AgentEnvironment.ForCurrentProcess(), NullAgentLogger.Instance, _unity);
        _host.Start();
    }

    private readonly TickingUnity _unity = new();

    public static TheoryData<string> SessionFixtureIds => new(Fixtures.Value.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(SessionFixtureIds))]
    public void The_agent_answers_the_fixture(string id)
    {
        var fixture = Fixtures.Value[id];
        var request = fixture.Request!;
        var method = ((JsonString)request["method"]!).Value;
        var handshake = method == Methods.Hello || fixture.Response!.TryGetValue("error", out var e) && ((JsonObject)e)["code"] is JsonString { Value: ErrorCodes.HandshakeRequired };

        using var peer = new Peer(_host.Transport!.Port!.Value);
        if (!handshake)
        {
            Assert.Null(peer.Call(Hello(_host.Token.Value)).Error);
        }

        // State the fixtures refer to: job j-1 (finished, so waits return at once) and activity entry 1.
        if (fixture.Group.StartsWith("job.", StringComparison.Ordinal))
        {
            var job = _host.Jobs.Start("survey", j =>
            {
                j.Progress("scan", 1, 1, "done");
                return null;
            });
            Assert.Equal("j-1", job.JobId);
            _host.Jobs.Wait(job.JobId, 5000, CancellationToken.None);
        }
        else if (fixture.Group.StartsWith("activity.", StringComparison.Ordinal))
        {
            Assert.Null(peer.Call(Ping()).Error);
        }
        else if (DataGroups.Contains(fixture.Group))
        {
            request = WithDataModel(request, id);
        }
        else if (fixture.Group.StartsWith("code.", StringComparison.Ordinal) && !id.EndsWith("/index-stale", StringComparison.Ordinal))
        {
            var page = peer.Call(Request(Methods.CodeTypes, "{\"limit\":1}"));
            request = WithCode(request, ((JsonString)((JsonObject)page.Result!)["cursor"]!).Value);
        }

        var response = peer.Call(WithToken(request, _host.Token.Value));
        var expected = (ResponseEnvelope)Envelope.Parse(fixture.Response!);
        Assert.Equal(expected.Id, response.Id);
        Assert.Equal(expected.Error?.Code, response.Error?.Code);
        Assert.Equal(expected.Context?.ToString(), response.Context?.ToString());

        var problems = ProtocolSchemas.Validate(JsonValue.Parse(response.ToUtf8Bytes()).ToString(), "envelope.schema.json");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        if (response.Error is null)
        {
            problems = ProtocolSchemas.Validate(response.Result!.ToString(), $"methods/{method}.schema.json#/$defs/result");
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        if (handshake && response.Error is not null)
        {
            Assert.Null(peer.Receive()); // a failed handshake closes the connection
        }
        else
        {
            Assert.Null(peer.Call(Ping()).Error); // everything else keeps it open
        }
    }

    [Fact]
    public void Encoded_values_match_the_value_schema()
    {
        var everything = new Zoo.Everything { owner = _unity.World.Create("Owner") };
        everything.onUse.AddPersistent(everything.owner, "Use");
        var root = new Target { H = _host.Data.Handles.Mint(everything) };
        var views = new[]
        {
            new UnityRuntimeAnalysisAgent.Core.Data.ViewOptions(),
            new UnityRuntimeAnalysisAgent.Core.Data.ViewOptions { Depth = 1, MaxItems = 1, MaxString = 3, MaxMembers = 20, Properties = true, Anchors = true },
            new UnityRuntimeAnalysisAgent.Core.Data.ViewOptions { Depth = 8, Enumerate = true },
        };
        foreach (var view in views)
        {
            var value = _host.Data.Writer(view, 1).Write(everything, new UnityRuntimeAnalysisAgent.Core.Data.Place { Root = root });
            var problems = ProtocolSchemas.Validate(value.ToString(), "common/value.schema.json");
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }
    }

    [Fact]
    public void The_survey_and_il_index_files_match_their_schemas()
    {
        var hero = _unity.World.Create("Hero");
        hero.AddComponent<Zoo.Survey.Enemy>();
        using var peer = new Peer(_host.Transport!.Port!.Value);
        Assert.Null(peer.Call(Hello(_host.Token.Value)).Error);
        foreach (var (method, file, schema) in new[]
        {
            (Methods.SurveyStart, "survey.ndjson", "files/survey.schema.json"),
            (Methods.IlIndexStart, "il.ndjson", "files/il_index.schema.json"),
        })
        {
            var path = Path.Combine(_providers, file);
            var extra = method == Methods.SurveyStart ? ",\"countInstances\":true,\"serializerMarkers\":{\"attributes\":[\"Zoo.Survey.FixtureSerializedAttribute\"]}" : ",\"records\":[\"calls\",\"fieldAccess\",\"strings\",\"allocations\",\"typeRefs\",\"tokens\"]";
            var started = peer.Call(Request(method, $"{{\"outFile\":{new JsonString(path)},\"include\":[\"UnityRuntimeAnalysisAgent.TestAssemblies\"]{extra}}}"));
            Assert.Null(started.Error);
            var job = _host.Jobs.Wait(JobRef.Read(started.Result, "result").JobId, 60_000, CancellationToken.None);
            Assert.True(job.State == "succeeded", $"{method}: {job.State} {job.Error?.Message}");
            var jobResult = ProtocolSchemas.Validate(job.Result!.ToString(), $"methods/{method}.schema.json#/$defs/jobResult");
            Assert.True(jobResult.Count == 0, string.Join(Environment.NewLine, jobResult));
            foreach (var line in File.ReadLines(path))
            {
                var problems = ProtocolSchemas.Validate(line, schema);
                Assert.True(problems.Count == 0, line + Environment.NewLine + string.Join(Environment.NewLine, problems));
            }
        }
    }

    [Fact]
    public void Assembly_loaded_events_match_their_schema()
    {
        using var peer = new Peer(_host.Transport!.Port!.Value);
        Assert.Null(peer.Call(Hello(_host.Token.Value)).Error);
        Assert.Null(peer.Call(Request(Methods.EventsSubscribe, "{\"kinds\":[\"code.assemblyLoaded\"]}")).Error);

        // A build the process hasn't seen: the test assembly with a new MVID and name (same lengths).
        var bytes = File.ReadAllBytes(typeof(Zoo.Plain).Assembly.Location);
        var mvid = typeof(Zoo.Plain).Module.ModuleVersionId.ToByteArray();
        var at = bytes.AsSpan().IndexOf(mvid);
        Guid.NewGuid().ToByteArray().CopyTo(bytes, at);
        var name = System.Text.Encoding.ASCII.GetBytes("UnityRuntimeAnalysisAgent.TestAssemblies");
        for (var i = bytes.AsSpan().IndexOf(name); i >= 0; i = bytes.AsSpan().IndexOf(name))
        {
            System.Text.Encoding.ASCII.GetBytes("UnityRuntimeAnalysisAgent.TestAssemblieq").CopyTo(bytes, i);
        }

        var context = new System.Runtime.Loader.AssemblyLoadContext("conformance", isCollectible: true);
        try
        {
            context.LoadFromStream(new MemoryStream(bytes));
            var ev = peer.ReceiveEvent(EventKinds.CodeAssemblyLoaded);
            var problems = ProtocolSchemas.Validate(ev.Params.ToString(), "events/code.assemblyLoaded.schema.json#/$defs/params");
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
            Assert.Contains("TestAssemblieq", ev.Params.ToString());
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void Job_events_match_their_schemas()
    {
        using var peer = new Peer(_host.Transport!.Port!.Value);
        Assert.Null(peer.Call(Hello(_host.Token.Value)).Error);
        Assert.Null(peer.Call(Request(Methods.EventsSubscribe, "{\"kinds\":[\"job.progress\",\"job.finished\"]}")).Error);
        _host.Jobs.Start("survey", j =>
        {
            j.Progress("scan", 1, 10, "first");
            return null;
        }, (JsonObject)JsonValue.Parse("{\"finding\":\"F-1\"}"));

        var progress = peer.ReceiveEvent(EventKinds.JobProgress);
        var finished = peer.ReceiveEvent(EventKinds.JobFinished);
        foreach (var ev in new[] { progress, finished })
        {
            var problems = ProtocolSchemas.Validate(JsonValue.Parse(ev.ToUtf8Bytes()).ToString(), "envelope.schema.json").ToList();
            problems.AddRange(ProtocolSchemas.Validate(ev.Params.ToString(), $"events/{ev.Method}.schema.json#/$defs/params"));
            Assert.True(problems.Count == 0, ev.Method + ": " + string.Join(Environment.NewLine, problems));
            Assert.Equal("{\"finding\":\"F-1\"}", ev.Context!.ToString());
        }
    }

    [Fact]
    public void The_package_manifest_matches_its_schema()
    {
        var build = Path.Combine(_providers, "plugin-build");
        Directory.CreateDirectory(build);
        foreach (var name in UnityRuntimeAnalysisAgent.Packaging.PackageBuilder.ShippedAssemblies)
        {
            File.WriteAllText(Path.Combine(build, name), name);
        }

        var dist = Path.Combine(_providers, "dist");
        UnityRuntimeAnalysisAgent.Packaging.PackageBuilder.Build(new(build, dist, "0.1.0-dev", "0.1", null));
        var problems = ProtocolSchemas.Validate(File.ReadAllText(Path.Combine(dist, "package.json")), "files/package.schema.json");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_session_fixtures_are_all_there()
    {
        Assert.Equal(SessionGroups.Order(StringComparer.Ordinal), Fixtures.Value.Values.Select(f => f.Group).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_discovery_file_matches_its_schema()
    {
        var text = File.ReadAllText(_host.DiscoveryPath!);
        var problems = ProtocolSchemas.Validate(text, "files/discovery.schema.json");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Agent_info_and_capabilities_match_their_schemas_with_everything_filled_in()
    {
        using var peer = new Peer(_host.Transport!.Port!.Value);
        foreach (var (method, result) in new[] { (Methods.Hello, peer.Call(Hello(_host.Token.Value))), (Methods.AgentInfo, peer.Call(Request(Methods.AgentInfo))), (Methods.AgentCapabilities, peer.Call(Request(Methods.AgentCapabilities))) })
        {
            Assert.Null(result.Error);
            var problems = ProtocolSchemas.Validate(result.Result!.ToString(), $"methods/{method}.schema.json#/$defs/result");
            Assert.True(problems.Count == 0, method + ": " + string.Join(Environment.NewLine, problems));
        }
    }

    private static JsonObject Request(string method, string parameters = "{}") =>
        (JsonObject)JsonValue.Parse($"{{\"v\":0,\"id\":\"t-{Guid.NewGuid():N}\",\"kind\":\"request\",\"method\":\"{method}\",\"params\":{parameters}}}");

    private static JsonObject Ping() => Request(Methods.Ping);

    private static JsonObject Hello(string token) =>
        Request(Methods.Hello, $"{{\"token\":\"{token}\",\"client\":{{\"name\":\"conformance\",\"version\":\"0.1\"}},\"protocol\":{{\"major\":{ProtocolVersion.Major},\"minor\":{ProtocolVersion.Minor}}}}}");

    private static readonly string[] DataGroups =
    [
        "handles.list", "handles.release", "handles.releaseAll", "vars.set", "vars.get", "vars.list", "vars.delete", "value.expand",
        "locator.resolve", "code.resolve",
    ];

    /// <summary>
    /// The state the data-model fixtures refer to: handle 481 is an <c>Example.Inventory</c> on <c>Main/Player</c> (with lower
    /// handles in use), the variable <c>Player</c> holds it, and the example ref and cursor become real ones. For the
    /// handle-expired fixtures the component is destroyed.
    /// </summary>
    private JsonObject WithDataModel(JsonObject request, string id)
    {
        var inventory = _unity.World.Create("Player").AddComponent<Example.Inventory>();
        while (_host.Data.Handles.Count < 480)
        {
            _host.Data.Handles.Mint(new object());
        }

        Assert.Equal(481, _host.Data.Handles.Mint(inventory));
        _host.Data.Variables.SetHandle("Player", 481);
        if (id.EndsWith("/handle-expired", StringComparison.Ordinal))
        {
            UnityEngine.Object.Destroy(inventory);
        }

        var writer = _host.Data.Writer(new UnityRuntimeAnalysisAgent.Core.Data.ViewOptions { MaxItems = 1 }, 1);
        var list = (JsonObject)writer.Write(inventory.items, new UnityRuntimeAnalysisAgent.Core.Data.Place { Root = new Target { H = 481 } }.Then(new MemberPathStep { Name = "items" }, ".items"));
        var reference = ((JsonString)((JsonObject)((JsonObject)((JsonArray)list["items"]!)[1])["redacted"]!)["ref"]!).Value;
        var text = request.ToString()
            .Replace("\"example-ref\"", $"\"{reference}\"", StringComparison.Ordinal)
            .Replace("\"example-cursor\"", $"\"{_host.Data.Cursors.Mint(0L)}\"", StringComparison.Ordinal);
        return (JsonObject)JsonValue.Parse(text);
    }

    /// <summary>
    /// The code fixtures use one example anchor for everything: each is replaced by a real anchor of the kind its parameter
    /// needs (a type, a method, a field), the example MVID by the test assembly's and the example cursor by a real one. The
    /// index-stale fixtures keep theirs.
    /// </summary>
    private static JsonObject WithCode(JsonObject request, string cursor)
    {
        static JsonValue Anchor(System.Reflection.MemberInfo member) => UnityRuntimeAnalysisAgent.Core.Data.AnchorWriter.ForMember(member).ToJson();
        var replacements = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
        {
            ["type"] = Anchor(typeof(Example.Inventory)),
            ["baseType"] = Anchor(typeof(UnityEngine.MonoBehaviour)),
            ["implements"] = Anchor(typeof(Zoo.Il.IShape)),
            ["method"] = Anchor(typeof(Zoo.Il.Corpus).GetMethod(nameof(Zoo.Il.Corpus.Twice))!),
            ["member"] = Anchor(typeof(Example.Inventory).GetField("items")!),
            ["field"] = Anchor(typeof(Zoo.Il.Corpus).GetField(nameof(Zoo.Il.Corpus.counter))!),
        };
        if (request["params"] is not JsonObject parameters)
        {
            return request;
        }

        var replaced = new JsonObject();
        foreach (var (key, value) in parameters)
        {
            replaced.Add(key, key switch
            {
                _ when value is JsonObject anchor && anchor.ContainsKey("mvid") && replacements.TryGetValue(key, out var real) => real,
                "methods" => new JsonArray([replacements["method"]]),
                "mvid" => JsonValue.From(typeof(Zoo.Plain).Module.ModuleVersionId.ToString()),
                "cursor" => JsonValue.From(cursor),
                _ => value,
            });
        }

        var copy = (JsonObject)JsonValue.Parse(request.ToString());
        copy.Set("params", replaced);
        return copy;
    }

    /// <summary>Substitutes the live session token for the fixture's example token; an all-zero token stays (it's the wrong-token case).</summary>
    private static JsonObject WithToken(JsonObject request, string token)
    {
        var text = request.ToString();
        if (request.TryGetValue("params", out var p) && p is JsonObject parameters && parameters.TryGetValue("token", out var t) && t is JsonString { Value: var example } && example.Trim('0').Length > 0)
        {
            text = text.Replace($"\"{example}\"", $"\"{token}\"", StringComparison.Ordinal);
        }

        return (JsonObject)JsonValue.Parse(text);
    }

    public void Dispose()
    {
        _host.Shutdown();
        _unity.Dispose();
        if (Directory.Exists(_providers))
        {
            Directory.Delete(_providers, recursive: true);
        }
    }

    private sealed class Peer : IDisposable
    {
        private readonly TcpClient _tcp = new() { NoDelay = true };
        private readonly FrameReader _reader;
        private readonly FrameWriter _writer;

        public Peer(int port)
        {
            _tcp.Connect(IPAddress.Loopback, port);
            _tcp.ReceiveTimeout = 5000;
            _reader = new FrameReader(_tcp.GetStream());
            _writer = new FrameWriter(_tcp.GetStream());
        }

        private readonly List<EventEnvelope> _events = new();

        public ResponseEnvelope Call(JsonObject request)
        {
            _writer.WriteFrame(request.ToUtf8Bytes());
            while (true)
            {
                var envelope = Receive();
                if (envelope is EventEnvelope ev)
                {
                    _events.Add(ev);
                    continue;
                }

                return Assert.IsType<ResponseEnvelope>(envelope);
            }
        }

        public EventEnvelope ReceiveEvent(string kind)
        {
            var queued = _events.FirstOrDefault(e => e.Method == kind);
            if (queued is not null)
            {
                _events.Remove(queued);
                return queued;
            }

            while (true)
            {
                var envelope = Receive() ?? throw new InvalidOperationException("closed");
                if (envelope is EventEnvelope ev && ev.Method == kind)
                {
                    return ev;
                }

                if (envelope is EventEnvelope other)
                {
                    _events.Add(other);
                }
            }
        }

        public Envelope? Receive()
        {
            try
            {
                return _reader.ReadFrame() is { } frame ? Envelope.Parse(frame) : null;
            }
            catch (IOException e) when (e.InnerException is not SocketException { SocketErrorCode: SocketError.TimedOut })
            {
                return null; // reset by the agent
            }
        }

        public void Dispose() => _tcp.Dispose();
    }

    /// <summary>A minimal Unity stand-in: the pump host ticks on a timer (about 200 frames per second).</summary>
    private sealed class TickingUnity : UnityRuntimeAnalysisAgent.Core.Abstractions.IUnityApi, IDisposable
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private volatile bool _alive;
        private long _frame;

        public bool IsPumpHostAlive => _alive;

        // One thread plays Unity's main thread: a frame every 5 ms, never two at once, and independent of the thread pool.
        public void CreatePumpHost(Action tick, Action endOfFrame)
        {
            _alive = true;
            new Thread(() =>
            {
                while (_alive)
                {
                    Interlocked.Increment(ref _frame);
                    tick();
                    endOfFrame();
                    Thread.Sleep(5);
                }
            })
            { IsBackground = true, Name = "fake main thread" }.Start();
        }

        public void RecreatePumpHost()
        {
        }

        public void DestroyPumpHost() => Dispose();

        public UnityRuntimeAnalysisAgent.Core.Abstractions.FrameTime ReadFrameTime()
        {
            var seconds = _clock.Elapsed.TotalSeconds;
            return new(Interlocked.Read(ref _frame), seconds, seconds, seconds, 1, 0.005);
        }

        public bool IsDestroyed(object unityObject) => FakeWorld.IsDestroyed(unityObject);

        public FakeWorld World { get; } = new();

        public UnityRuntimeAnalysisAgent.Core.Abstractions.UnityObjectFacts? Describe(object unityObject) =>
            FakeWorld.Describe(unityObject) is { } facts ? new(facts.InstanceId, facts.Name) : null;

        public object? FindGameObject(string path, string? scene) => World.Find(path, scene);

        public object? GetComponent(object gameObjectOrComponent, Type componentType) => FakeWorld.GetComponent(gameObjectOrComponent, componentType);

        public object? FindChild(object gameObjectOrComponent, string path) => FakeWorld.FindChild(gameObjectOrComponent, path);

        public UnityRuntimeAnalysisAgent.Core.Abstractions.SceneAddress? Locate(object unityObject) =>
            FakeWorld.Locate(unityObject) is { } at ? new(at.Scene, at.Path) : null;

        public IReadOnlyDictionary<Type, int> CountObjectsByType(Type baseType) => World.CountByType(baseType);

        public void Dispose() => _alive = false;
    }
}
