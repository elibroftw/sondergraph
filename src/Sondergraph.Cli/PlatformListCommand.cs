namespace Sondergraph.Cli;

/// <summary>
/// Implements <c>sg ingest platforms</c>: lists the parsers in this build.
/// </summary>
/// <remarks>
/// Split into its own helper rather than growing <see cref="IngestCommands"/>,
/// which already carries the argument parsing and reporting.
/// </remarks>
internal static partial class IngestCommands
{
    /// <summary>Lists the registered parsers and what they recognise.</summary>
    internal static int ListPlatforms()
    {
        Console.WriteLine("Registered parsers");
        Console.WriteLine();

        foreach (var parser in Parsers)
        {
            Console.WriteLine($"  {parser.Platform,-12} {parser.DisplayName}  (v{parser.ParserVersion})");
        }

        Console.WriteLine();
        Console.WriteLine("Usage: sg ingest parse <path> [--platform <name>] [--dry-run] [--wait]");

        return ExitCode.Success;
    }
}
