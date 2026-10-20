using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.Cli;

/// <summary>
/// Implements <c>sg tier</c>, <c>sg tag</c>, <c>sg locate</c> and <c>sg rename</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is a writer, so each takes the workspace lock (DESIGN.md §5). They are
/// grouped here because they share the same shape: resolve the person, take the lock, apply
/// one change, save.
/// </para>
/// <para>
/// Separate commands rather than one <c>sg edit --field value</c>: the four operations are
/// short enough to type, and a single generic editor invites typos in field names that fail
/// at runtime instead of at the prompt.
/// </para>
/// </remarks>
internal static class EditCommands
{
    /// <summary>One-line descriptions of the relationship tiers, for help output.</summary>
    internal static readonly (string Name, ContactTier Tier, string Description)[] TierChoices =
    [
        ("acquaintance", ContactTier.Acquaintance, "someone you know of"),
        ("friend", ContactTier.Friend, "a friend"),
        ("close", ContactTier.Close, "a close friend"),
        ("best", ContactTier.Best, "a best friend"),
        ("family", ContactTier.Family, "family"),
        ("none", ContactTier.Unknown, "clear the rating"),
    ];

    internal static int Tier(string[] args)
    {
        // sg tier <person> <tier>
        var (personArg, value, workspaceArg) = ParsePair(args, "tier");

        if (personArg is null || value is null)
        {
            Console.Error.WriteLine("sg: usage: sg tier <person|id> <tier>");
            Console.Error.WriteLine($"sg: tiers: {string.Join(", ", TierChoices.Select(t => t.Name))}");
            return ExitCode.Usage;
        }

        if (!PersonDocument.TryParseTierInput(value, out var tier))
        {
            Console.Error.WriteLine($"sg: unknown tier '{value}'.");
            Console.Error.WriteLine($"sg: tiers: {string.Join(", ", TierChoices.Select(t => t.Name))}");
            return ExitCode.Usage;
        }

        return WithPerson(workspaceArg, personArg, "sg tier", (editor, store, person) =>
        {
            var result = editor.SetTier(person.Id, tier);

            if (!result.IsOk)
            {
                ConsoleRenderer.WriteFailure(result);
                return ExitCode.MapExitCode(result.Code);
            }

            Console.WriteLine(
                tier == ContactTier.Unknown
                    ? $"Cleared the relationship for {person.Name}."
                    : $"{person.Name} is now {PersonDocument.DescribeTier(tier)}.");

            return ExitCode.Success;
        });
    }

    internal static int Preferred(string[] args)
    {
        // sg preferred <person> [<channel>] [--clear]
        var clear = args.Contains("--clear", StringComparer.Ordinal);
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");

        // Skip flag VALUES as well as flags. Taking every non-flag token treated the
        // --workspace path as the channel, so reading it back reported the path as the value.
        var positional = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--workspace" or "-w")
            {
                i++;
                continue;
            }

            if (!args[i].StartsWith('-'))
            {
                positional.Add(args[i]);
            }
        }

        var personArg = positional.FirstOrDefault();
        var channel = positional.Count > 1 ? positional[1] : null;

        if (personArg is null)
        {
            Console.Error.WriteLine("sg: usage: sg preferred <person|id> [<channel>] [--clear]");
            return ExitCode.Usage;
        }

        var located = Workspace.Locate(workspaceArg);

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var store = new PersonStore(located.ValueOrThrow());

        // No channel and no --clear: show the current value and the channels already in use,
        // so the user can see the vocabulary before inventing one.
        if (channel is null && !clear)
        {
            var found = PersonCommands.FindPerson(store, personArg);

            if (!found.IsOk)
            {
                ConsoleRenderer.WriteFailure(found);
                return ExitCode.MapExitCode(found.Code);
            }

            var person = found.ValueOrThrow();

            Console.WriteLine(person.PreferredChannel is null
                ? $"{person.Name} has no preferred channel."
                : $"{person.Name} prefers {PreferredChannels.Display(person.PreferredChannel)}.");

            var everyone = store.ListAll(out _);

            if (everyone.IsOk)
            {
                var inUse = PreferredChannels.InUse(everyone.ValueOrThrow());

                if (inUse.Count > 0)
                {
                    Console.WriteLine(
                        $"  in use: {string.Join(", ", inUse.Select(PreferredChannels.Display))}");
                }
            }

            return ExitCode.Success;
        }

        return WithPerson(workspaceArg, personArg, "sg preferred", (editor, edStore, person) =>
        {
            // Warn about a near-match before writing. On the command line this is the only
            // chance to see the typo next to the channel that was meant.
            if (!clear && channel is not null)
            {
                var everyone = edStore.ListAll(out _);
                var inUse = everyone.IsOk ? PreferredChannels.InUse(everyone.ValueOrThrow()) : [];

                if (PreferredChannels.SuggestExisting(channel, inUse) is { } suggestion)
                {
                    Console.Error.WriteLine(
                        $"sg: warning: '{channel}' looks like '{PreferredChannels.Display(suggestion)}', "
                        + "which is already in use.");
                    Console.Error.WriteLine(
                        "sg: re-run the same command to set it anyway if that spelling is intended.");
                }
            }

            var result = editor.SetPreferredChannel(person.Id, clear ? null : channel);

            if (!result.IsOk)
            {
                ConsoleRenderer.WriteFailure(result);
                return ExitCode.MapExitCode(result.Code);
            }

            Console.WriteLine(clear
                ? $"Cleared the preferred channel for {person.Name}."
                : $"{person.Name} prefers {PreferredChannels.Display(channel)}.");

            return ExitCode.Success;
        });
    }

    internal static int Tag(string[] args)
    {
        // sg tag <person> [<tag>] [--remove]
        var remove = args.Contains("--remove", StringComparer.Ordinal) || args.Contains("-r", StringComparer.Ordinal);
        var positional = args.Skip(1).Where(a => !a.StartsWith('-')).ToList();

        var personArg = positional.FirstOrDefault();
        var tagValue = positional.Count > 1 ? positional[1] : null;
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");

        if (personArg is null)
        {
            Console.Error.WriteLine("sg: usage: sg tag <person|id> [<tag>] [--remove]");
            return ExitCode.Usage;
        }

        // No tag given: list what they have, which is the natural thing to want next.
        if (tagValue is null)
        {
            var located = Workspace.Locate(workspaceArg);

            if (!located.IsOk)
            {
                ConsoleRenderer.WriteFailure(located);
                return ExitCode.MapExitCode(located.Code);
            }

            var store = new PersonStore(located.ValueOrThrow());
            var found = PersonCommands.FindPerson(store, personArg);

            if (!found.IsOk)
            {
                ConsoleRenderer.WriteFailure(found);
                return ExitCode.MapExitCode(found.Code);
            }

            var person = found.ValueOrThrow();

            if (person.Tags.Count == 0)
            {
                Console.WriteLine($"{person.Name} has no tags.");
                return ExitCode.Success;
            }

            Console.WriteLine($"{person.Name}: {string.Join(", ", person.Tags)}");
            return ExitCode.Success;
        }

        return WithPerson(workspaceArg, personArg, "sg tag", (editor, store, person) =>
        {
            var result = remove
                ? editor.RemoveTag(person.Id, tagValue)
                : editor.AddTag(person.Id, tagValue);

            if (!result.IsOk)
            {
                ConsoleRenderer.WriteFailure(result);
                return ExitCode.MapExitCode(result.Code);
            }

            // Read back what was actually stored, so the confirmation shows normalisation
            // the user did not type ("Big Client" becoming "big-client").
            var reloaded = store.Load(person.Id);
            var tags = reloaded.IsOk ? reloaded.ValueOrThrow().Tags : [];

            Console.WriteLine(remove
                ? $"Removed '{tagValue}' from {person.Name}."
                : $"Tagged {person.Name} with '{tagValue}'.");

            if (tags.Count > 0)
            {
                Console.WriteLine($"  now: {string.Join(", ", tags)}");
            }

            return ExitCode.Success;
        });
    }

    internal static int Locate(string[] args)
    {
        // sg locate <person> <place> [--kind home|work|other] [--remove]
        var remove = args.Contains("--remove", StringComparer.Ordinal) || args.Contains("-r", StringComparer.Ordinal);
        var kindArg = ValueAfter(args, "--kind") ?? ValueAfter(args, "-k");
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");

        var positional = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--kind" or "-k" or "--workspace" or "-w")
            {
                i++; // skip the flag's value
                continue;
            }

            if (!args[i].StartsWith('-'))
            {
                positional.Add(args[i]);
            }
        }

        var personArg = positional.FirstOrDefault();
        var place = positional.Count > 1 ? positional[1] : null;

        if (personArg is null || place is null)
        {
            Console.Error.WriteLine("sg: usage: sg locate <person|id> <place> [--kind home|work|other] [--remove]");
            return ExitCode.Usage;
        }

        var kind = LocationKind.Other;

        if (!string.IsNullOrWhiteSpace(kindArg))
        {
            kind = kindArg.Trim().ToLowerInvariant() switch
            {
                "home" => LocationKind.Home,
                "work" => LocationKind.Work,
                "other" => LocationKind.Other,
                _ => (LocationKind)(-1),
            };

            if ((int)kind < 0)
            {
                Console.Error.WriteLine($"sg: unknown location kind '{kindArg}'.");
                Console.Error.WriteLine("sg: kinds: home, work, other");
                return ExitCode.Usage;
            }
        }

        return WithPerson(workspaceArg, personArg, "sg locate", (editor, store, person) =>
        {
            var result = remove
                ? editor.RemoveLocation(person.Id, place)
                : editor.AddLocation(person.Id, place, kind);

            if (!result.IsOk)
            {
                ConsoleRenderer.WriteFailure(result);
                return ExitCode.MapExitCode(result.Code);
            }

            Console.WriteLine(remove
                ? $"Removed '{place}' from {person.Name}."
                : $"Recorded '{place}' for {person.Name} ({PersonDocument.LocationToWire(kind)}).");

            return ExitCode.Success;
        });
    }

    internal static int Rename(string[] args)
    {
        // sg rename <person> <new name>
        var positional = args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");

        if (positional.Count < 2)
        {
            Console.Error.WriteLine("sg: usage: sg rename <person|id> <new name>");
            return ExitCode.Usage;
        }

        var personArg = positional[0];
        var newName = string.Join(' ', positional.Skip(1));

        return WithPerson(workspaceArg, personArg, "sg rename", (editor, store, person) =>
        {
            var result = editor.Rename(person.Id, newName);

            if (!result.IsOk)
            {
                ConsoleRenderer.WriteFailure(result);
                return ExitCode.MapExitCode(result.Code);
            }

            Console.WriteLine($"Renamed {person.Name} to {newName.Trim()}.");
            Console.WriteLine($"  id unchanged: {person.Id}");
            return ExitCode.Success;
        });
    }

    /// <summary>
    /// Resolves the workspace and person, takes the lock, applies a change.
    /// </summary>
    /// <remarks>
    /// The person is resolved <em>before</em> taking the lock, so a typo fails immediately
    /// rather than after waiting on a busy workspace.
    /// </remarks>
    private static int WithPerson(
        string? workspaceArg,
        string personArg,
        string command,
        Func<PersonEditor, PersonStore, Person, int> change)
    {
        var located = Workspace.Locate(workspaceArg);

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var workspace = located.ValueOrThrow();
        var store = new PersonStore(workspace);

        var found = PersonCommands.FindPerson(store, personArg);

        if (!found.IsOk)
        {
            ConsoleRenderer.WriteFailure(found);
            return ExitCode.MapExitCode(found.Code);
        }

        using var acquired = WorkspaceLock.TryAcquire(workspace, command);

        if (!acquired.IsOk)
        {
            ConsoleRenderer.WriteFailure(acquired);
            return ExitCode.MapExitCode(acquired.Code);
        }

        return change(new PersonEditor(store), store, found.ValueOrThrow());
    }

    private static (string? Person, string? Value, string? Workspace) ParsePair(string[] args, string command)
    {
        var positional = args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");

        return (
            positional.FirstOrDefault(),
            positional.Count > 1 ? positional[1] : null,
            workspaceArg);
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
