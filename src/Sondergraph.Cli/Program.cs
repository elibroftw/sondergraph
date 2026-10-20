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
        if (args.Length == 0)
        {
            PrintUsage();
            return ExitCode.Success;
        }

        return args[0] switch
        {
            "--version" or "-v" => PrintVersion(),
            "--help" or "-h" => PrintUsage(),
            "init" => WorkspaceCommands.Init(args),
            "ingest" => DispatchIngest(args),
            "list" or "ls" => PersonCommands.List(args),
            "show" => PersonCommands.Show(args),
            "stats" => PersonCommands.Stats(args),
            "note" or "notes" => NoteCommands.Run(args),
            "tier" or "relationship" => EditCommands.Tier(args),
            "tag" or "tags" => EditCommands.Tag(args),
            "locate" or "location" => EditCommands.Locate(args),
            "preferred" or "channel" => EditCommands.Preferred(args),
            "met" or "via" => EditCommands.Met(args),
            "rename" => EditCommands.Rename(args),
            "graft" or "merge" => MergeCommands.Graft(args),
            "ungraft" or "unmerge" => MergeCommands.Ungraft(args),
            "merges" => MergeCommands.ListMerges(args),
            "workspace" or "ws" => DispatchWorkspace(args),
            var unknown => Unknown(unknown),
        };
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"sg {SondergraphVersion.Version} (protocol {SondergraphVersion.Protocol})");
        return ExitCode.Success;
    }

    private static int DispatchIngest(string[] args)
    {
        // sg ingest parse <path>
        if (args.Length < 2)
        {
            Console.Error.WriteLine("sg: 'ingest' needs a subcommand.");
            Console.Error.WriteLine("sg: available: parse");
            return ExitCode.Usage;
        }

        return args[1] switch
        {
            "parse" or "import" => IngestCommands.Parse(args),
            "platforms" => IngestCommands.ListPlatforms(),
            var unknown => Unknown($"ingest {unknown}"),
        };
    }

    private static int DispatchWorkspace(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("sg: 'workspace' needs a subcommand.");
            Console.Error.WriteLine("sg: available: status, lock");
            return ExitCode.Usage;
        }

        return args[1] switch
        {
            "status" => WorkspaceCommands.Status(args),
            "lock" => WorkspaceCommands.LockInfo(args),
            var unknown => Unknown($"workspace {unknown}"),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"sg: unknown command '{command}'");
        Console.Error.WriteLine("sg: run 'sg --help' for usage.");
        return ExitCode.Usage;
    }

    private static int PrintUsage()
    {
        Console.WriteLine($"sg {SondergraphVersion.Version} — sondergraph, everything all at once.");
        Console.WriteLine();
        Console.WriteLine("Usage: sg <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Workspace:");
        Console.WriteLine("  init [path]            Create a workspace (default: current directory).");
        Console.WriteLine("  workspace status       Show the workspace and its lock state.");
        Console.WriteLine("  workspace lock         Show details of the current lock holder.");
        Console.WriteLine();
        Console.WriteLine("Contacts:");
        Console.WriteLine("  list [path]            List every person, sorted by name.");
        Console.WriteLine("  show <id|name>         Show one person in detail.");
        Console.WriteLine("  stats                  Summarise the contact graph.");
        Console.WriteLine("  note <person>          Print a contact's note.");
        Console.WriteLine("  note <person> --edit   Edit it in $EDITOR.");
        Console.WriteLine("  note <person> --set    Replace it with the given text.");
        Console.WriteLine("  note <person> --clear  Delete it.");
        Console.WriteLine("  tier <person> <tier>   Set the relationship (friend, close, best, ...).");
        Console.WriteLine("  tag <person> <tag>     Add a tag (--remove to drop one).");
        Console.WriteLine("  locate <person> <place>  Record a location (--kind home|work|other).");
        Console.WriteLine("  preferred <person> [ch]  How they prefer to be contacted (--clear to unset).");
        Console.WriteLine("  met <person> <how>     How you know them, e.g. work, university.");
        Console.WriteLine("  rename <person> <name> Rename, keeping the id.");
        Console.WriteLine();
        Console.WriteLine("Merging:");
        Console.WriteLine("  graft <absorb> <keep>  Merge two contacts into one (the second keeps its id).");
        Console.WriteLine("  ungraft <merge-id>     Reverse a merge.");
        Console.WriteLine("  merges                 List recorded merges.");
        Console.WriteLine();
        Console.WriteLine("Ingest:");
        Console.WriteLine("  ingest parse <path>    Read an export and materialise people.");
        Console.WriteLine("  ingest platforms       List the parsers in this build.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -h, --help             Show this help.");
        Console.WriteLine("  -v, --version          Show version.");
        Console.WriteLine();
        Console.WriteLine("Publishing (Hugo + RSS) is not implemented yet; see tasks/ for the backlog.");
        return ExitCode.Success;
    }
}
