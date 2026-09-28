using System.Net;
using System.Net.Sockets;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Conformance;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Framing;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Protocol.Tests;

/// <summary>
/// Sends the pinned protocol's session fixtures (handshake, liveness, info, capabilities, cancel, subscriptions) to a real
/// in-process agent and checks its responses: same outcome and error code as the fixture, results valid against the schemas,
/// and the connection closed after a failed handshake.
/// </summary>
public sealed class SessionConformanceTests : IDisposable
{
    private static readonly string[] SessionGroups = ["hello", "ping", "agent.info", "agent.capabilities", "cancel", "events.subscribe", "events.unsubscribe", "_generic"];

    private static readonly Lazy<IReadOnlyDictionary<string, FixtureCase>> Fixtures = new(() =>
        FixtureCatalog.Load(Path.Combine(ProtocolSchemas.Root, "fixtures", "agent")).Where(f => SessionGroups.Contains(f.Group)).ToDictionary(f => f.Id));

    private readonly string _providers = Path.Combine(Path.GetTempPath(), "uraa-tests", Guid.NewGuid().ToString("N"));
    private readonly AgentHost _host;

    public SessionConformanceTests()
    {
        var config = new DictionaryConfigSource().Set(AgentConfig.TransportModeKey, "tcp").Set(AgentConfig.ProvidersDirKey, _providers);
        _host = new AgentHost(config, AgentEnvironment.ForCurrentProcess(), NullAgentLogger.Instance);
        _host.Start();
    }

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

        public ResponseEnvelope Call(JsonObject request)
        {
            _writer.WriteFrame(request.ToUtf8Bytes());
            return Assert.IsType<ResponseEnvelope>(Receive());
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
}
