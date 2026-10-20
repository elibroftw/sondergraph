using System.Diagnostics;
using Sondergraph.Core;
using Sondergraph.Ingest;
using Sondergraph.Ingest.Parsers;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.Cli;

/// <summary>
/// Implements <c>sg ingest</c>: read a raw dump and materialise people.
/// </summary>
/// <remarks>
/// Thin, per ADR 0001. All the behaviour lives in <c>Sondergraph.Ingest</c>; this parses
/// arguments, reports progress, and renders the outcome.
/// </remarks>
internal static partial class IngestCommands
{
    /// <summary>Parsers known to this build.</summary>
    /// <remarks>
    /// A hard-coded registry, per ADR 0003 — no dynamic assembly loading. Each parser
    /// declares the platform it handles, and the source is offered to each in turn.
    /// </remarks>
    internal static IReadOnlyList<IPlatformParser> Parsers { get; } =
    [
        new VCardParser(),
        new LinkedinParser(),
        new FacebookParser(),
        new InstagramParser(),
    ];

    internal static int Parse(string[] args)
    {
        // sg ingest parse <path> [--platform <name>] [--dry-run] [--wait]
        var options = IngestOptions.Parse(args);

        if (options.Path is null)
        {
            Console.Error.WriteLine("sg: 'ingest parse' needs a path to a dump or export.");
            Console.Error.WriteLine("sg: usage: sg ingest parse <path> [--platform <name>] [--dry-run]");
            return ExitCode.Usage;
        }

        if (!File.Exists(options.Path) && !Directory.Exists(options.Path))
        {
            Console.Error.WriteLine($"sg: '{options.Path}' does not exist.");
            return ExitCode.Usage;
        }

        var located = Workspace.Locate(options.WorkspacePath);
        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var workspace = located.ValueOrThrow();

        // Writers take the lock; readers do not (DESIGN.md §5).
        using var acquired = WorkspaceLock.TryAcquire(workspace, "sg ingest parse");

        if (!acquired.IsOk)
        {
            if (acquired.Code == ErrorCode.WorkspaceLocked && options.Wait)
            {
                var waited = WorkspaceLock
                    .AcquireAsync(workspace, TimeSpan.FromSeconds(60), "sg ingest parse")
                    .GetAwaiter()
                    .GetResult();

                if (!waited.IsOk)
                {
                    ConsoleRenderer.WriteFailure(waited);
                    return ExitCode.MapExitCode(waited.Code);
                }

                return RunWithLock(workspace, waited.ValueOrThrow(), options);
            }

            ConsoleRenderer.WriteFailure(acquired);
            return ExitCode.MapExitCode(acquired.Code);
        }

        return RunWithLock(workspace, acquired.ValueOrThrow(), options);
    }

    private static int RunWithLock(Workspace workspace, WorkspaceLock held, IngestOptions options)
    {
        using var _ = held;

        // Open the source. Zips are preferred where available: an extracted tree can be
        // partial, and reading one previously hid 179 contacts (docs/platforms/README.md).
        IRawSource? source = null;
        IDisposable? disposable = null;

        if (File.Exists(options.Path) && options.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var opened = ZipRawSource.Open(options.Path);

            if (!opened.IsOk)
            {
                ConsoleRenderer.WriteFailure(opened);
                return ExitCode.MapExitCode(opened.Code);
            }

            source = opened.ValueOrThrow();
            disposable = (IDisposable)source;
        }
        else if (Directory.Exists(options.Path))
        {
            // A directory containing zips is treated as one logical export. This is the
            // common case for Google Takeout, which splits an export across several
            // archives by size and does not split them predictably by service.
            // Recursive, not top-level only. Exports are frequently unzipped into a
            // subdirectory (a Takeout download lands in takeout/, for instance), so a
            // top-level scan silently finds nothing and reports an empty import.
            var archives = Directory
                .EnumerateFiles(options.Path, "*.zip", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (archives.Count > 0)
            {
                var multi = MultiZipRawSource.Open(archives, options.Path!);
                source = multi;
                disposable = multi;

                Console.WriteLine($"Found {archives.Count} archive(s) in {options.Path}");

                foreach (var problem in multi.Problems)
                {
                    // A truncated part is a common failure and must not be mistaken for a
                    // complete export. Report it loudly but carry on with what is readable.
                    Console.Error.WriteLine($"sg: warning: unreadable archive {problem}");
                }
            }
            else
            {
                source = new DirectoryRawSource(options.Path!);
            }
        }
        else
        {
            source = new DirectoryRawSource(options.Path!);
        }

        try
        {
            var parser = SelectParser(source, options.Platform);

            if (parser is null)
            {
                Console.Error.WriteLine(
                    $"sg: no parser recognised '{options.Path}'"
                    + (options.Platform is null ? "." : $" as platform '{options.Platform}'."));
                Console.Error.WriteLine(
                    $"sg: known platforms: {string.Join(", ", Parsers.Select(p => p.Platform))}");
                return ExitCode.Usage;
            }

            Console.WriteLine($"Parsing {source.Description}");
            Console.WriteLine($"  parser: {parser.DisplayName} v{parser.ParserVersion}");

            var context = new ParseContext();
            var stopwatch = Stopwatch.StartNew();
            var records = parser.Parse(source, context).ToList();
            stopwatch.Stop();

            var accounts = records.OfType<ObservedAccount>().ToList();
            var interactions = records.OfType<ObservedInteraction>().Count();

            Console.WriteLine(
                $"  found:  {PersonDocument.People(accounts.Count)}"
                + (interactions > 0 ? $", {interactions} interactions" : string.Empty)
                + $" in {stopwatch.ElapsedMilliseconds} ms");

            if (context.Problems.Count > 0)
            {
                Console.WriteLine($"  skipped {context.Problems.Count} item(s):");

                foreach (var group in context.Problems
                    .GroupBy(p => p.Reason)
                    .OrderByDescending(g => g.Count())
                    .Take(5))
                {
                    Console.WriteLine($"    {group.Count(),5}  {group.Key}");
                }
            }

            if (options.DryRun)
            {
                Console.WriteLine();
                Console.WriteLine("Dry run: nothing was written.");
                return ExitCode.Success;
            }

            var store = new PersonStore(workspace);
            var logResult = MergeLog.Load(workspace.Root);

            if (!logResult.IsOk)
            {
                ConsoleRenderer.WriteFailure(logResult);
                return ExitCode.MapExitCode(logResult.Code);
            }

            var materialiser = new Materialiser(store, logResult.ValueOrThrow());

            Console.WriteLine();
            Console.WriteLine("Writing to the workspace...");

            var written = 0;
            var materialised = materialiser
                .MaterialiseAsync(records, _ =>
                {
                    written++;

                    // Progress on a single rewriting line, so a 552-record ingest does not
                    // scroll the useful summary off the screen.
                    if (written % 25 == 0)
                    {
                        Console.Write($"\r  {written} processed...");
                    }
                })
                .GetAwaiter()
                .GetResult();

            Console.Write("\r");

            if (!materialised.IsOk)
            {
                ConsoleRenderer.WriteFailure(materialised);
                return ExitCode.MapExitCode(materialised.Code);
            }

            var report = materialised.ValueOrThrow();
            PrintReport(report, workspace);

            return ExitCode.Success;
        }
        finally
        {
            disposable?.Dispose();
        }
    }

    private static void PrintReport(MaterialiseReport report, Workspace workspace)
    {
        Console.WriteLine("Done.");
        Console.WriteLine($"  people created:  {report.PeopleCreated}");
        Console.WriteLine($"  people enriched: {report.PeopleUpdated}");
        Console.WriteLine($"  records merged:  {report.RecordsMerged}");

        if (report.RecordsSkipped > 0)
        {
            Console.WriteLine($"  records skipped: {report.RecordsSkipped} (no usable identifier)");
        }

        if (report.Candidates.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"{report.Candidates.Count} possible duplicate(s) found - not merged automatically.");
            Console.WriteLine("Review with 'sg dupes', merge with 'sg graft <a> <b>'.");

            foreach (var candidate in report.Candidates.Take(5))
            {
                Console.WriteLine($"    {candidate.LeftId}  <->  {candidate.RightId}");
            }

            if (report.Candidates.Count > 5)
            {
                Console.WriteLine($"    ... and {report.Candidates.Count - 5} more");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Workspace: {workspace.Root}");
        Console.WriteLine("List them with 'sg list'.");
    }

    /// <summary>Chooses a parser for the source.</summary>
    private static IPlatformParser? SelectParser(IRawSource source, string? requested)
    {
        if (requested is not null)
        {
            return Parsers.FirstOrDefault(p =>
                string.Equals(p.Platform, requested, StringComparison.OrdinalIgnoreCase));
        }

        // First parser that recognises the source. With one parser this is trivial; with
        // several, registration order decides, which is why the list is ordered explicitly.
        return Parsers.FirstOrDefault(p => p.CanParse(source));
    }
}

/// <summary>Parsed command-line options for <c>sg ingest parse</c>.</summary>
internal sealed record IngestOptions
{
    internal string? Path { get; init; }

    internal string? Platform { get; init; }

    internal string? WorkspacePath { get; init; }

    internal bool DryRun { get; init; }

    internal bool Wait { get; init; }

    /// <summary>
    /// Parses arguments, skipping the leading <c>ingest parse</c>.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than pulling in an argument library. The surface is small and
    /// stable, and a dependency here would be the only one in the CLI. Revisit when the
    /// command count makes this genuinely unwieldy.
    /// </remarks>
    internal static IngestOptions Parse(string[] args)
    {
        string? path = null;
        string? platform = null;
        string? workspacePath = null;
        var dryRun = false;
        var wait = false;

        for (var i = 2; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "--platform" or "-p" when i + 1 < args.Length:
                    platform = args[++i];
                    break;

                case "--workspace" or "-w" when i + 1 < args.Length:
                    workspacePath = args[++i];
                    break;

                case "--dry-run" or "-n":
                    dryRun = true;
                    break;

                case "--wait":
                    wait = true;
                    break;

                default:
                    if (!arg.StartsWith('-') && path is null)
                    {
                        path = arg;
                    }

                    break;
            }
        }

        return new IngestOptions
        {
            Path = path,
            Platform = platform,
            WorkspacePath = workspacePath,
            DryRun = dryRun,
            Wait = wait,
        };
    }
}
