using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.Cli;

/// <summary>
/// Suggests who to invite to an event.
/// </summary>
/// <remarks>
/// Prints suggestions and writes nothing. Turning a suggestion into a guest list is a
/// separate, deliberate act, and the planner is careful never to do it implicitly.
/// </remarks>
internal static class PartyCommands
{
    /// <summary>
    /// Routes <c>sg party ...</c> to the planner or to suggestions.
    /// </summary>
    /// <remarks>
    /// "sg guest" is kept as an alias so existing habits and scripts keep working; it routes
    /// to the same place.
    /// </remarks>
    internal static int Dispatch(string[] args)
    {
        var subcommand = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));

        return subcommand switch
        {
            null or "list" or "ls" or "show" or "add" or "create" or "remove" or "rm" =>
                GuestCommands.Run(args),
            "suggest" => Run(args),
            _ => Run(args),
        };
    }
    internal static int Run(string[] args)
    {
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");
        var located = Workspace.Locate(workspaceArg);

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var loaded = new PersonStore(located.ValueOrThrow()).ListAll(out _);

        if (!loaded.IsOk)
        {
            ConsoleRenderer.WriteFailure(loaded);
            return ExitCode.MapExitCode(loaded.Code);
        }

        var everyone = args.Contains("--everyone", StringComparer.Ordinal);
        var places = ValuesAfter(args, "--here").DefaultIfEmpty(ValueAfter(args, "--location") ?? string.Empty)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        // Coordinates come from data/gazetteer.json, resolved once by "sg geo --resolve".
        // A missing gazetteer is not an error: the plan simply has no distance signal.
        var gazetteer = new GazetteerStore(located.ValueOrThrow()).Load();
        var placesWithCoords = gazetteer.IsOk ? gazetteer.ValueOrThrow() : Gazetteer.Empty;
        var venue = placesWithCoords.Find(places.FirstOrDefault());

        var options = new PartyPlanOptions(
            everyone ? CohortScope.Everyone : CohortScope.Location,
            places,
            FemaleShare: double.TryParse(ValueAfter(args, "--female-share"), out var share) ? share : 0.51,
            Capacity: int.TryParse(ValueAfter(args, "--capacity"), out var cap) ? cap : 20,
            Waves: int.TryParse(ValueAfter(args, "--waves"), out var waves) ? waves : 3,
            Seed: int.TryParse(ValueAfter(args, "--seed"), out var seed) ? seed : 1,
            Venue: venue,
            Gazetteer: placesWithCoords);

        if (!everyone && places.Count == 0)
        {
            Console.Error.WriteLine("sg: usage: sg party --here <place> [--capacity N] [--waves N]");
            Console.Error.WriteLine("sg:        sg party --everyone  [--capacity N] [--waves N]");
            return ExitCode.Usage;
        }

        var plan = InvitePlanner.Plan(loaded.ValueOrThrow(), options);

        Console.WriteLine("Party suggestions (nothing is created)");
        Console.WriteLine();

        foreach (var group in plan.ByWave)
        {
            Console.WriteLine($"  Wave {group.Key}");
            Console.WriteLine();

            foreach (var suggestion in group.OrderByDescending(s => s.Weight))
            {
                var tier = suggestion.Person.Tier == ContactTier.Unknown
                    ? "unrated"
                    : suggestion.TierLabel;

                var where = suggestion.DistanceKm is { } km
                    ? $"{km:N0} km"
                    : string.Empty;

                Console.WriteLine(
                    $"    {suggestion.Person.Name,-34} {tier,-14} {suggestion.Person.Gender ?? "-",-3} {where}");
            }

            Console.WriteLine();
        }

        Console.WriteLine($"  total: {plan.Count} ({plan.Female} F, {plan.Male} M, {plan.UnrecordedGender} unrecorded)");
        Console.WriteLine();

        if (!everyone && venue is null)
        {
            Console.WriteLine(
                "  note: no coordinates for the venue, so distance did not affect the order.");
            Console.WriteLine("        Run 'sg geo --resolve' to look them up.");
            Console.WriteLine();
        }

        foreach (var note in plan.Notes)
        {
            Console.WriteLine($"  note: {note}");
        }

        Console.WriteLine();
        Console.WriteLine("  Add these to a list with: sg guest add <list> <person>...");

        return ExitCode.Success;
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static IEnumerable<string> ValuesAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag && !args[i + 1].StartsWith('-'))
            {
                yield return args[i + 1];
            }
        }
    }
}
