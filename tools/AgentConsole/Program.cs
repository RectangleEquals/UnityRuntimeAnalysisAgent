namespace UnityRuntimeAnalysisAgent.AgentConsole;

internal static class Program
{
    private static Task<int> Main(string[] args) =>
        ConsoleApp.RunAsync(args, Console.In, Console.Out, colors: !Console.IsOutputRedirected);
}
