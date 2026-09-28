using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Client;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;
using UnityRuntimeAnalysisAgent.Core.Transport;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Hosting;

public sealed class AgentHostTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _providers = Path.Combine(Path.GetTempPath(), "uraa-tests", Guid.NewGuid().ToString("N"));
    private readonly List<AgentHost> _hosts = new();

    private AgentHost StartHost(string transport, Func<string, IAgentLogger, ITransport>? createPipe = null, TestLogger? log = null, bool providers = true)
    {
        var config = new DictionaryConfigSource().Set(AgentConfig.TransportModeKey, transport);
        if (providers)
        {
            config.Set(AgentConfig.ProvidersDirKey, _providers);
        }

        var environment = AgentEnvironment.ForCurrentProcess();
        environment.UnityVersion = "2021.3.45f1";
        var host = new AgentHost(config, environment, log ?? new TestLogger(), pipeName: $"uraa-test-{Guid.NewGuid():N}", createPipe: createPipe);
        _hosts.Add(host);
        host.Start();
        return host;
    }

    private static Task<AgentClient> Connect(AgentHost host) => host.Transport!.Kind == "pipe"
        ? AgentClient.ConnectPipeAsync(host.Transport.PipeName!, host.Token.Value, "test")
        : AgentClient.ConnectTcpAsync(host.Transport.Port!.Value, host.Token.Value, "test");

    [Theory]
    [InlineData("pipe")]
    [InlineData("tcp")]
    public async Task A_client_connects_authenticates_and_pings(string transport)
    {
        var host = StartHost(transport);
        Assert.Equal(transport, host.Transport!.Kind);
        await using var client = await Connect(host);
        Assert.Equal(transport, client.Info.Transport);
        Assert.Equal("2021.3.45f1", client.Info.UnityVersion);
        var ping = PingResult.Read(await client.CallAsync(Methods.Ping, (JsonObject)JsonValue.Parse("{\"echo\":\"x\"}"), Ct), "result");
        Assert.Equal("x", ping.Echo);
    }

    [Theory]
    [InlineData("pipe")]
    [InlineData("tcp")]
    public async Task Four_clients_at_once_and_events_reach_subscribers_only(string transport)
    {
        var host = StartHost(transport);
        var clients = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Connect(host)));
        await clients[0].CallAsync(Methods.EventsSubscribe, (JsonObject)JsonValue.Parse("{\"kinds\":[\"agent.warning\"]}"), Ct);
        host.Publish(EventKinds.AgentWarning, new AgentWarningParams { Code = "TEST", Message = "hello" });
        var ev = await clients[0].Events.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(EventKinds.AgentWarning, ev.Method);
        Assert.Equal(1, ev.Seq);
        Assert.False(clients[1].Events.TryRead(out _));
        Assert.Equal(4, host.ConnectionCount);

        if (transport == "pipe")
        {
            // A fifth client waits for a free slot.
            var fifth = Connect(host);
            await Task.Delay(300, Ct);
            Assert.False(fifth.IsCompleted);
            await clients[3].DisposeAsync();
            await using var connected = await fifth.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Equal("pipe", connected.Info.Transport);
        }

        foreach (var client in clients)
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task Auto_falls_back_to_tcp_when_the_pipe_fails()
    {
        var log = new TestLogger();
        var host = StartHost("auto", (_, _) => throw new IOException("pipes are unavailable"), log);
        Assert.Equal("tcp", host.Transport!.Kind);
        Assert.True(log.Contains(AgentLogLevel.Warning, "falling back to TCP"));
        await using var client = await Connect(host);
        Assert.Equal("tcp", client.Info.Transport);
    }

    [Fact]
    public void Pipe_mode_does_not_fall_back()
    {
        Assert.Throws<IOException>(() => StartHost("pipe", (_, _) => throw new IOException("pipes are unavailable")));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void The_pipe_is_restricted_to_the_current_user_where_supported()
    {
        var host = StartHost("pipe");
        var pipe = Assert.IsType<PipeTransport>(host.Transport);
        Assert.True(pipe.AclApplied, pipe.AclFailure);

        using var probe = new NamedPipeClientStream(".", pipe.PipeName!, PipeDirection.InOut);
        probe.Connect(5000);
        var rules = probe.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var me = WindowsIdentity.GetCurrent().User;
        Assert.NotEmpty(rules);
        Assert.All(rules, r =>
        {
            Assert.Equal(me, r.IdentityReference);
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
        });
    }

    [Fact]
    public async Task The_discovery_file_is_published_and_removed()
    {
        var host = StartHost("tcp");
        var path = Assert.IsType<string>(host.DiscoveryPath);
        Assert.Equal(Path.Combine(_providers, $"agent-{Environment.ProcessId}.json"), path);
        var file = DiscoveryFile.Read(JsonValue.Parse(await File.ReadAllBytesAsync(path, Ct)), "file");
        Assert.Equal(host.Token.Value, file.Token);
        Assert.Equal(host.Transport!.Port, file.Port);
        Assert.Null(file.Pipe);
        Assert.Equal(AgentMode.ReadOnly, file.Mode);
        Assert.Equal(ProtocolVersion.Major, file.Protocol.Major);
        Assert.False(File.Exists(path + ".tmp"));

        await using (var client = await AgentClient.ConnectDiscoveryAsync(path, "test", Ct))
        {
            Assert.Equal(Environment.ProcessId, client.Info.Pid);
        }

        host.Shutdown();
        host.Shutdown(); // idempotent
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Without_a_providers_directory_nothing_is_published()
    {
        var log = new TestLogger();
        var host = StartHost("tcp", log: log, providers: false);
        Assert.Null(host.DiscoveryPath);
        Assert.True(log.Contains(AgentLogLevel.Warning, "Discovery.ProvidersDir is not set"));
        Assert.False(Directory.Exists(_providers));
    }

    [Fact]
    public async Task Shutdown_closes_connections()
    {
        var host = StartHost("pipe");
        await using var client = await Connect(host);
        host.Shutdown();
        await Assert.ThrowsAsync<AgentClientException>(() => client.CallAsync(Methods.Ping, cancellationToken: Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(0, host.ConnectionCount);
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Shutdown();
        }

        if (Directory.Exists(_providers))
        {
            Directory.Delete(_providers, recursive: true);
        }
    }
}
