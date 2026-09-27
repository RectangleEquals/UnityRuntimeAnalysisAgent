namespace UnityRuntimeAnalysisAgent.AgentConsole;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.Error.WriteLine("AgentConsole: the protocol client is not implemented yet.");
        return args.Length == 0 ? 0 : 1;
    }
}
