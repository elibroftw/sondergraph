using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.Cli;

/// <summary>
/// Implements <c>sg graft</c> (merge) and <c>sg ungraft</c> (unmerge).
/// </summary>
/// <remarks>
/// <para>
/// Named after the botanical operation rather than "merge": a graft joins two organisms and
/// the join is recorded, which is exactly what happens here — and it reads well in a project
/// whose vocabulary is otherwise about growth.
/// </para>
/// <para>
/// Both are writers, so both take the workspace lock.
/// </para>
/// </remarks>
internal static class MergeCommands
{
    internal static int Graft(string[] args)
    {
        // sg graft <absorbed> <into> [--dry-run]
        var dryRun = args.Contains("--dry-run", StringComparer.Ordinal) || args.Contains("-n", StringComparer.Ordinal);
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");
        var positional = args.Skip(1).Where(a => !a.StartsWith('-')).ToList();

        if (positional.Count < 2)
        {
            Console.Error.WriteLine("sg: usage: sg graft <person-to-absorb> <person-to-keep> [--dry-run]");
            Console.Error.WriteLine("sg: the FIRST person is merged into the SECOND, which keeps its id.");
            return ExitCode.Usage;
        }

        var absorbedArg = positional[0];
        var survivorArg = positional[1];

        var located = Workspace.Locate(workspaceArg);

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var workspace = located.ValueOrThrow();
        var store = new PersonStore(workspace);

        var absorbedFound = PersonCommands.FindPerson(store, absorbedArg);

        if (!absorbedFound.IsOk)
        {
            ConsoleRenderer.WriteFailure(absorbedFound);
            return ExitCode.MapExitCode(absorbedFound.Code);
        }

        var survivorFound = PersonCommands.FindPerson(store, survivorArg);

        if (!survivorFound.IsOk)
        {
            ConsoleRenderer.WriteFailure(survivorFound);
            return ExitCode.MapExitCode(survivorFound.Code);
        }

        var absorbed = absorbedFound.ValueOrThrow();
        var survivor = survivorFound.ValueOrThrow();

        if (dryRun)
        {
            // Preview the result without touching anything. Merging is destructive enough
            // that being able to look first is worth the extra flag.
            var preview = MergeService.Combine(survivor, absorbed);

            Console.WriteLine("Dry run: nothing was written.");
            Console.WriteLine();
            Console.WriteLine($"Would merge '{absorbed.Name}' ({absorbed.Id}) into '{survivor.Name}' ({survivor.Id}):");
            Console.WriteLine();
            DescribePerson("result", preview);
            return ExitCode.Success;
        }

        using var acquired = WorkspaceLock.TryAcquire(workspace, "sg graft");

        if (!acquired.IsOk)
        {
            ConsoleRenderer.WriteFailure(acquired);
            return ExitCode.MapExitCode(acquired.Code);
        }

        var logResult = MergeLog.Load(workspace.Root);

        if (!logResult.IsOk)
        {
            ConsoleRenderer.WriteFailure(logResult);
            return ExitCode.MapExitCode(logResult.Code);
        }

        var service = new MergeService(store, logResult.ValueOrThrow());
        var merged = service.Merge(survivor.Id, absorbed.Id);

        if (!merged.IsOk)
        {
            ConsoleRenderer.WriteFailure(merged);
            return ExitCode.MapExitCode(merged.Code);
        }

        var record = merged.ValueOrThrow();

        Console.WriteLine($"Merged '{absorbed.Name}' into '{survivor.Name}'.");
        Console.WriteLine();

        var reloaded = store.Load(survivor.Id);

        if (reloaded.IsOk)
        {
            DescribePerson("now", reloaded.ValueOrThrow());
        }

        Console.WriteLine();
        Console.WriteLine($"Merge id: {record.Id}");
        Console.WriteLine($"Undo with: sg ungraft {record.Id}");

        return ExitCode.Success;
    }

    internal static int Ungraft(string[] args)
    {
        // sg ungraft <merge-id>
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");
        var mergeId = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));

        if (mergeId is null)
        {
            Console.Error.WriteLine("sg: usage: sg ungraft <merge-id>");
            Console.Error.WriteLine("sg: list recent merges with: sg merges");
            return ExitCode.Usage;
        }

        var located = Workspace.Locate(workspaceArg);

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var workspace = located.ValueOrThrow();

        using var acquired = WorkspaceLock.TryAcquire(workspace, "sg ungraft");

        if (!acquired.IsOk)
        {
            ConsoleRenderer.WriteFailure(acquired);
            return ExitCode.MapExitCode(acquired.Code);
        }

        var logResult = MergeLog.Load(workspace.Root);

        if (!logResult.IsOk)
        {
            ConsoleRenderer.WriteFailure(logResult);
            return ExitCode.MapExitCode(logResult.Code);
        }

        var service = new MergeService(new PersonStore(workspace), logResult.ValueOrThrow());
        var undone = service.Undo(mergeId);

        if (!undone.IsOk)
        {
            ConsoleRenderer.WriteFailure(undone);
            return ExitCode.MapExitCode(undone.Code);
        }

        Console.WriteLine($"Reversed merge {mergeId}. Both people are separate again.");
        return ExitCode.Success;
    }

    internal static int Links(string[] args)
    {
        // sg links [--min-score 0.6] [--limit 20]
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");
        var limit = int.TryParse(ValueAfter(args, "--limit"), out var l) ? l : 20;
        var minScore = double.TryParse(ValueAfter(args, "--min-score"), out var m) ? m : 0.5;

        var located = Workspace.Locate(workspaceArg);

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var store = new PersonStore(located.ValueOrThrow());
        var loaded = store.ListAll(out _);

        if (!loaded.IsOk)
        {
            ConsoleRenderer.WriteFailure(loaded);
            return ExitCode.MapExitCode(loaded.Code);
        }

        var candidates = LinkCandidates.Find(loaded.ValueOrThrow(), minScore);

        if (candidates.Count == 0)
        {
            Console.WriteLine("No cross-platform candidates found.");
            return ExitCode.Success;
        }

        Console.WriteLine($"{candidates.Count} possible link(s); showing the top {Math.Min(limit, candidates.Count)}:");
        Console.WriteLine();

        foreach (var candidate in candidates.Take(limit))
        {
            // "Left ⟷ Right" on one line, evidence beneath, then the graft command so the
            // next step needs no looking up.
            Console.WriteLine(
                $"  {candidate.Left.Name} ({string.Join(",", candidate.Left.Sources)})"
                + $"  <->  {candidate.Right.Name} ({string.Join(",", candidate.Right.Sources)})");

            var reasons = string.Join("; ", candidate.Evidence.Select(e => e.Detail));
            var adds = $"  combines: {candidate.PlatformSummary}";

            Console.WriteLine($"      {reasons}{adds}");
            Console.WriteLine($"      sg graft {candidate.Right.Id} {candidate.Left.Id}");
            Console.WriteLine();
        }

        return ExitCode.Success;
    }

    internal static int ListMerges(string[] args)
    {
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");
        var located = Workspace.Locate(workspaceArg);

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var workspace = located.ValueOrThrow();
        var logResult = MergeLog.Load(workspace.Root);

        if (!logResult.IsOk)
        {
            ConsoleRenderer.WriteFailure(logResult);
            return ExitCode.MapExitCode(logResult.Code);
        }

        var log = logResult.ValueOrThrow();

        if (log.Records.Count == 0)
        {
            Console.WriteLine("No merges recorded.");
            return ExitCode.Success;
        }

        Console.WriteLine($"Merges ({log.Records.Count}):");
        Console.Write(log.Describe());
        return ExitCode.Success;
    }

    /// <summary>Prints the parts of a person a merge would affect.</summary>
    private static void DescribePerson(string label, Person person)
    {
        Console.WriteLine($"  {label}:");

        var tier = person.Tier == ContactTier.Unknown ? "-" : PersonDocument.DescribeTier(person.Tier);
        Console.WriteLine($"    name:      {person.Name}  (id {person.Id})");
        Console.WriteLine($"    tier:      {tier}");

        Console.WriteLine($"    phones:    {(person.Phones.Count == 0 ? "-" : string.Join(", ", person.Phones))}");
        Console.WriteLine($"    emails:    {(person.Emails.Count == 0 ? "-" : string.Join(", ", person.Emails))}");
        Console.WriteLine($"    accounts:  {(person.Accounts.Count == 0 ? "-" : string.Join(", ", person.Accounts.Select(a => $"{a.Platform}:{a.Handle}")))}");
        Console.WriteLine($"    tags:      {(person.Tags.Count == 0 ? "-" : string.Join(", ", person.Tags))}");
        Console.WriteLine($"    locations: {(person.Locations.Count == 0 ? "-" : string.Join(", ", person.Locations.Select(l => l.Place)))}");
        Console.WriteLine($"    sources:   {(person.Sources.Count == 0 ? "-" : string.Join(", ", person.Sources))}");
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
