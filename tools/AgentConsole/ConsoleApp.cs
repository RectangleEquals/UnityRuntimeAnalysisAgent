using System.Text.Json;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Client;

namespace UnityRuntimeAnalysisAgent.AgentConsole;

/// <summary>
/// The console's logic, separate from <c>Main</c> so tests can drive it.
/// <code>
/// AgentConsole (--discovery &lt;file&gt; | --pipe &lt;name&gt; --token &lt;hex&gt; | --tcp &lt;port&gt; --token &lt;hex&gt;) [--raw] [command]
/// commands: info · send &lt;method&gt; [&lt;json&gt; | @file] · subscribe &lt;kind&gt;[,&lt;kind&gt;…] · script &lt;file&gt; · wait &lt;ms&gt; · quit
/// </code>
/// Without a command it reads commands from standard input, one per line. Events are printed as they arrive.
/// </summary>
internal sealed class ConsoleApp
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly TextWriter _out;
    private readonly bool _colors;
    private readonly bool _raw;
    private readonly object _outputLock = new();

    private ConsoleApp(TextWriter output, bool colors, bool raw)
    {
        _out = output;
        _colors = colors;
        _raw = raw;
    }

    public const string Usage =
        "Usage: AgentConsole (--discovery <file> | --pipe <name> --token <hex> | --tcp <port> --token <hex>) [--raw] [command]\n" +
        "Commands: info | send <method> [<json> | @file] | subscribe <kind>[,<kind>...] | script <file> | wait <ms> | quit";

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, bool colors)
    {
        string? discovery = null, pipe = null, token = null;
        int? port = null;
        var raw = false;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--discovery" when i + 1 < args.Length: discovery = args[++i]; break;
                case "--pipe" when i + 1 < args.Length: pipe = args[++i]; break;
                case "--tcp" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
                case "--token" when i + 1 < args.Length: token = args[++i]; break;
                case "--raw": raw = true; break;
                case "--help" or "-h": output.WriteLine(Usage); return 0;
                default: rest.Add(args[i]); break;
            }
        }

        var app = new ConsoleApp(output, colors, raw);
        AgentClient client;
        try
        {
            if (discovery is not null)
            {
                client = await AgentClient.ConnectDiscoveryAsync(discovery);
            }
            else if (pipe is not null && token is not null)
            {
                client = await AgentClient.ConnectPipeAsync(pipe, token);
            }
            else if (port is not null && token is not null)
            {
                client = await AgentClient.ConnectTcpAsync(port.Value, token);
            }
            else
            {
                output.WriteLine(Usage);
                return 2;
            }
        }
        catch (Exception e) when (e is AgentClientException or IOException or TimeoutException or System.Net.Sockets.SocketException)
        {
            app.Print(ConsoleColor.Red, $"Couldn't connect: {Describe(e)}");
            return 1;
        }

        await using (client)
        {
            client.EventReceived += ev => app.Print(ConsoleColor.Cyan, $"event {ev.Method} #{ev.Seq}", ev.Params);
            app.Print(ConsoleColor.Green, $"connected: agent {client.Info.AgentVersion}, protocol {client.Info.Protocol.Major}.{client.Info.Protocol.Minor}, mode {UnityLudometry.Protocol.AgentModes.ToWire(client.Info.Mode)}");
            if (rest.Count > 0)
            {
                return await app.ExecuteAsync(client, string.Join(" ", rest)) ? 0 : 1;
            }

            var ok = true;
            while (await input.ReadLineAsync() is { } line)
            {
                if (line.Trim() is "quit" or "exit")
                {
                    break;
                }

                ok &= await app.ExecuteAsync(client, line);
            }

            return ok ? 0 : 1;
        }
    }

    private async Task<bool> ExecuteAsync(AgentClient client, string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            return true;
        }

        var space = line.IndexOf(' ');
        var command = space < 0 ? line : line[..space];
        var argument = space < 0 ? string.Empty : line[(space + 1)..].Trim();
        try
        {
            switch (command)
            {
                case "info":
                    return await SendAsync(client, UnityLudometry.Protocol.Methods.AgentInfo, string.Empty);
                case "send":
                    var methodEnd = argument.IndexOf(' ');
                    return methodEnd < 0
                        ? await SendAsync(client, argument, string.Empty)
                        : await SendAsync(client, argument[..methodEnd], argument[(methodEnd + 1)..].Trim());
                case "subscribe":
                    var kinds = argument.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var json = "{\"kinds\":[" + string.Join(",", kinds.Select(k => JsonSerializer.Serialize(k))) + "]}";
                    return await SendAsync(client, UnityLudometry.Protocol.Methods.EventsSubscribe, json);
                case "script":
                    var ok = true;
                    foreach (var scriptLine in await File.ReadAllLinesAsync(argument))
                    {
                        var parts = scriptLine.Trim();
                        if (parts.Length > 0 && !parts.StartsWith('#'))
                        {
                            ok &= await ExecuteAsync(client, parts.StartsWith("send ") || parts.StartsWith("subscribe ") || parts.StartsWith("wait ") ? parts : "send " + parts);
                        }
                    }

                    return ok;
                case "wait":
                    await Task.Delay(int.Parse(argument, System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                default:
                    Print(ConsoleColor.Red, $"Unknown command '{command}'.\n{Usage}");
                    return false;
            }
        }
        catch (Exception e) when (e is AgentClientException or UnityLudometry.Protocol.ProtocolException or FormatException or IOException or JsonException or InvalidCastException)
        {
            Print(ConsoleColor.Red, Describe(e));
            return false;
        }
    }

    private static string Describe(Exception e) => e is AgentClientException error ? $"{error.Code}: {error.Message}" : e.Message;

    private async Task<bool> SendAsync(AgentClient client, string method, string parameters)
    {
        if (parameters.StartsWith('@'))
        {
            parameters = await File.ReadAllTextAsync(parameters[1..]);
        }

        var json = parameters.Length == 0 ? null : (JsonObject)JsonValue.Parse(parameters);
        var response = await client.SendAsync(method, json);
        if (response.Error is { } error)
        {
            Print(ConsoleColor.Red, $"{method} → {error.Code}: {error.Message}", error.Data);
            return false;
        }

        Print(ConsoleColor.Green, $"{method} →", response.Result);
        return true;
    }

    private void Print(ConsoleColor color, string heading, JsonValue? body = null)
    {
        var text = body is null ? null : _raw ? body.ToString() : JsonSerializer.Serialize(JsonDocument.Parse(body.ToString()).RootElement, Indented);
        lock (_outputLock)
        {
            if (_colors)
            {
                Console.ForegroundColor = color;
            }

            _out.WriteLine(heading);
            if (_colors)
            {
                Console.ResetColor();
            }

            if (text is not null)
            {
                _out.WriteLine(text);
            }

            _out.Flush();
        }
    }
}
