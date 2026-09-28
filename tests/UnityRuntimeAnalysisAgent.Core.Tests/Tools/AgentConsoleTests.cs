using System.Text;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.AgentConsole;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Tools;

public sealed class AgentConsoleTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _providers = Path.Combine(Path.GetTempPath(), "uraa-tests", Guid.NewGuid().ToString("N"));
    private readonly AgentHost _host;

    public AgentConsoleTests()
    {
        var config = new DictionaryConfigSource().Set(AgentConfig.ProvidersDirKey, _providers).Set(AgentConfig.TransportModeKey, "pipe");
        _host = new AgentHost(config, AgentEnvironment.ForCurrentProcess(), new TestLogger(), pipeName: $"uraa-test-{Guid.NewGuid():N}");
        _host.Start();
    }

    private sealed class SyncWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_sb)
            {
                _sb.Append(value);
            }
        }

        public override string ToString()
        {
            lock (_sb)
            {
                return _sb.ToString();
            }
        }
    }

    [Fact]
    public async Task Handshake_ping_subscribe_and_receive_an_event()
    {
        var output = new SyncWriter();
        var input = new StringReader(string.Join("\n", "info", "send ping {\"echo\":\"hello from the console\"}", "subscribe agent.warning", "wait 3000", "quit"));
        var run = ConsoleApp.RunAsync(["--discovery", _host.DiscoveryPath!], input, output, colors: false);

        // Publish until the subscription is in place and the console has printed the event.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!output.ToString().Contains("event agent.warning", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            _host.Publish(EventKinds.AgentWarning, new AgentWarningParams { Code = "TEST_EVENT", Message = "from the test" });
            await Task.Delay(50, Ct);
        }

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        var text = output.ToString();
        Assert.Contains("connected: agent", text);
        Assert.Contains("agent.info →", text);
        Assert.Contains("\"echo\": \"hello from the console\"", text);
        Assert.Contains("\"subscribed\"", text);
        Assert.Contains("event agent.warning", text);
        Assert.Contains("TEST_EVENT", text);
    }

    [Fact]
    public async Task One_shot_commands_raw_output_and_errors()
    {
        var output = new SyncWriter();
        var args = new[] { "--pipe", _host.PipeName, "--token", _host.Token.Value, "--raw", "send", "ping", "{\"echo\":\"x\"}" };
        Assert.Equal(0, await ConsoleApp.RunAsync(args, TextReader.Null, output, colors: false));
        Assert.Contains("\"echo\":\"x\"", output.ToString());

        var errors = new SyncWriter();
        Assert.Equal(1, await ConsoleApp.RunAsync(["--pipe", _host.PipeName, "--token", _host.Token.Value, "send", "no.suchMethod"], TextReader.Null, errors, colors: false));
        Assert.Contains("METHOD_NOT_FOUND", errors.ToString());

        var refused = new SyncWriter();
        Assert.Equal(1, await ConsoleApp.RunAsync(["--pipe", _host.PipeName, "--token", new string('0', 64), "info"], TextReader.Null, refused, colors: false));
        Assert.Contains("BAD_TOKEN", refused.ToString(), StringComparison.Ordinal);

        var usage = new SyncWriter();
        Assert.Equal(2, await ConsoleApp.RunAsync([], TextReader.Null, usage, colors: false));
        Assert.Contains("Usage:", usage.ToString());
    }

    [Fact]
    public async Task Scripts_run_one_request_per_line()
    {
        var script = Path.Combine(_providers, "script.txt");
        await File.WriteAllTextAsync(script, "# comment\nping {\"echo\":\"1\"}\nsend ping {\"echo\":\"2\"}\n", Ct);
        var output = new SyncWriter();
        Assert.Equal(0, await ConsoleApp.RunAsync(["--discovery", _host.DiscoveryPath!, "script", script], TextReader.Null, output, colors: false));
        Assert.Contains("\"echo\": \"1\"", output.ToString());
        Assert.Contains("\"echo\": \"2\"", output.ToString());
    }

    public void Dispose()
    {
        _host.Shutdown();
        if (Directory.Exists(_providers))
        {
            Directory.Delete(_providers, recursive: true);
        }
    }
}
