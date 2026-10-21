using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.Cli;

/// <summary>
/// Party planner commands.
/// </summary>
/// <remarks>
/// Thin by design (ADR 0001): parsing arguments and printing results, with the work done in
/// <see cref="GuestListStore"/> and <see cref="ResolvedGuestList"/>.
/// </remarks>
internal static class GuestCommands
{
    /// <summary>Dispatches <c>sg party ...</c>.</summary>
    internal static int Run(string[] args)
    {
        var sub = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) ?? "list";

        return sub switch
        {
            "list" or "ls" => List(args),
            "show" => Show(args),
            "add" or "create" => Create(args),
            "remove" or "rm" => Remove(args),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("sg party list                       List parties.");
        Console.WriteLine("sg party show <name>                Show a party grouped by gender.");
        Console.WriteLine("sg party add <name> <person...>     Create or add guests to a party.");
        Console.WriteLine("sg party remove <name> <person>     Remove a guest from a party.");
        return ExitCode.Usage;
    }

    private static string? WorkspaceArg(string[] args) => ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");

    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Positional arguments, skipping flags and their values.</summary>
    private static List<string> Positional(string[] args)
    {
        var result = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--workspace" or "-w")
            {
                i++;
                continue;
            }

            if (!args[i].StartsWith('-'))
            {
                result.Add(args[i]);
            }
        }

        // The subcommand itself is the first positional.
        return result.Count > 0 ? result[1..] : result;
    }

    private static Result<Workspace> Locate(string[] args, out string? failure)
    {
        failure = null;
        var located = Workspace.Locate(WorkspaceArg(args));

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            failure = located.Message;
        }

        return located;
    }

    private static int List(string[] args)
    {
        var located = Locate(args, out _);

        if (!located.IsOk)
        {
            return ExitCode.MapExitCode(located.Code);
        }

        var lists = new GuestListStore(located.ValueOrThrow()).ListAll();

        if (!lists.IsOk)
        {
            ConsoleRenderer.WriteFailure(lists);
            return ExitCode.MapExitCode(lists.Code);
        }

        if (lists.ValueOrThrow().Count == 0)
        {
            Console.WriteLine("No parties yet. Create one with: sg party add <name> <person>...");
            return ExitCode.Success;
        }

        Console.WriteLine($"Parties ({lists.ValueOrThrow().Count}):");

        foreach (var list in lists.ValueOrThrow())
        {
            Console.WriteLine($"  {list.Slug,-24} {list.Name}  ({list.Count} guests)");
        }

        return ExitCode.Success;
    }

    private static int Show(string[] args)
    {
        var positional = Positional(args);

        if (positional.Count == 0)
        {
            Console.Error.WriteLine("sg: usage: sg party show <name>");
            return ExitCode.Usage;
        }

        var located = Locate(args, out _);

        if (!located.IsOk)
        {
            return ExitCode.MapExitCode(located.Code);
        }

        var loaded = new GuestListStore(located.ValueOrThrow()).Load(positional[0]);

        if (!loaded.IsOk)
        {
            ConsoleRenderer.WriteFailure(loaded);
            return ExitCode.MapExitCode(loaded.Code);
        }

        var people = new PersonStore(located.ValueOrThrow()).ListAll(out _);

        if (!people.IsOk)
        {
            ConsoleRenderer.WriteFailure(people);
            return ExitCode.MapExitCode(people.Code);
        }

        var byId = people.ValueOrThrow().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var list = loaded.ValueOrThrow();

        var guests = list.Members.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        var missing = list.Members.Where(id => !byId.ContainsKey(id)).ToList();
        var resolved = new ResolvedGuestList(list, guests, missing);

        Console.WriteLine($"{list.Name}  ({resolved.Count} guests)");

        if (!string.IsNullOrWhiteSpace(list.Notes))
        {
            Console.WriteLine($"  {list.Notes}");
        }

        Console.WriteLine();

        foreach (var group in resolved.Groups)
        {
            Console.WriteLine($"  {group.Label}  ({group.Count})");

            foreach (var person in group.People)
            {
                Console.WriteLine($"      {person.Name}");
            }
        }

        // Named rather than merely counted: a vanished guest is something the user can act on.
        if (missing.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  {missing.Count} entr(y/ies) no longer resolve: {string.Join(", ", missing)}");
        }

        return ExitCode.Success;
    }

    private static int Create(string[] args)
    {
        var positional = Positional(args);

        if (positional.Count < 2)
        {
            Console.Error.WriteLine("sg: usage: sg party add <name> <person> [<person>...]");
            return ExitCode.Usage;
        }

        var located = Locate(args, out _);

        if (!located.IsOk)
        {
            return ExitCode.MapExitCode(located.Code);
        }

        var store = new GuestListStore(located.ValueOrThrow());
        var personStore = new PersonStore(located.ValueOrThrow());
        var listName = positional[0];

        var existing = store.Load(listName);
        var members = existing.IsOk ? existing.ValueOrThrow().Members.ToList() : [];
        var added = 0;

        foreach (var query in positional.Skip(1))
        {
            var found = PersonCommands.FindPerson(personStore, query);

            if (!found.IsOk)
            {
                Console.Error.WriteLine($"sg: no one matches '{query}'.");
                continue;
            }

            var person = found.ValueOrThrow();

            if (members.Contains(person.Id, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {person.Name} is already on the list.");
                continue;
            }

            members.Add(person.Id);
            added++;
            Console.WriteLine($"  added {person.Name}");
        }

        var list = new GuestList(
            GuestList.SlugFor(listName),
            existing.IsOk ? existing.ValueOrThrow().Name : listName.Trim(),
            members,
            existing.IsOk ? existing.ValueOrThrow().Notes : null);

        var saved = store.Save(list);

        if (!saved.IsOk)
        {
            ConsoleRenderer.WriteFailure(saved);
            return ExitCode.MapExitCode(saved.Code);
        }

        Console.WriteLine();
        Console.WriteLine($"{list.Name}: {list.Count} guests ({added} added this run).");

        return ExitCode.Success;
    }

    private static int Remove(string[] args)
    {
        var positional = Positional(args);

        if (positional.Count < 2)
        {
            Console.Error.WriteLine("sg: usage: sg party remove <name> <person>");
            return ExitCode.Usage;
        }

        var located = Locate(args, out _);

        if (!located.IsOk)
        {
            return ExitCode.MapExitCode(located.Code);
        }

        var store = new GuestListStore(located.ValueOrThrow());
        var loaded = store.Load(positional[0]);

        if (!loaded.IsOk)
        {
            ConsoleRenderer.WriteFailure(loaded);
            return ExitCode.MapExitCode(loaded.Code);
        }

        var personStore = new PersonStore(located.ValueOrThrow());
        var list = loaded.ValueOrThrow();
        var members = list.Members.ToList();
        var removed = 0;

        foreach (var query in positional.Skip(1))
        {
            var found = PersonCommands.FindPerson(personStore, query);

            if (!found.IsOk)
            {
                Console.Error.WriteLine($"sg: no one matches '{query}'.");
                continue;
            }

            var person = found.ValueOrThrow();

            if (members.RemoveAll(id => string.Equals(id, person.Id, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                removed++;
                Console.WriteLine($"  removed {person.Name}");
            }
        }

        var saved = store.Save(list with { Members = members });

        if (!saved.IsOk)
        {
            ConsoleRenderer.WriteFailure(saved);
            return ExitCode.MapExitCode(saved.Code);
        }

        Console.WriteLine();
        Console.WriteLine($"{list.Name}: {members.Count} guests ({removed} removed this run).");

        return ExitCode.Success;
    }
}
