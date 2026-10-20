using Sondergraph.Core;

namespace Sondergraph.Cli;

/// <summary>
/// Command-line entry point for <c>sg</c>.
/// </summary>
/// <remarks>
/// Thin by design (ADR 0001): this parses arguments, calls into Core, and renders the
/// result. It must never contain logic that the API also needs — if something here
/// starts looking like business logic, it belongs in <c>Sondergraph.Core</c>.
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--version" or "-v":
                    Console.WriteLine($"sg {SondergraphVersion.Version}");
                    return 0;

                case "--help" or "-h":
                    PrintUsage();
                    return 0;

                default:
                    Console.Error.WriteLine($"sg: unknown command '{args[0]}'");
                    Console.Error.WriteLine("Run 'sg --help' for usage.");
                    return 2;
            }
        }

        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"sg {SondergraphVersion.Version} — sondergraph, everything all at once.");
        Console.WriteLine();
        Console.WriteLine("Usage: sg <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands are not implemented yet. See tasks/ for the backlog.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -h, --help      Show this help.");
        Console.WriteLine("  -v, --version   Show version.");
    }
}
