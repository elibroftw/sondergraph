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
        ("family", ContactTier.Family, "immediate family"),
        ("extended-family", ContactTier.ExtendedFamily, "extended family"),
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

    /// <summary>
    /// Deletes people whose only contact detail is a machine-generated address.
    /// </summary>
    /// <remarks>
    /// Dry run by default. These records look like contacts but are usually notification
    /// senders - a GitHub thread synced from a mailbox, named after the repository - so
    /// removing them is routine, but destructive enough to want previewing.
    /// </remarks>
    internal static int Purge(string[] args)
    {
        var apply = args.Contains("--yes", StringComparer.Ordinal);
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");
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

        var editor = new PersonEditor(store);
        var candidates = loaded.ValueOrThrow()
            .Where(p => p.Emails.Count > 0 && p.Emails.All(e => !ContactEmails.IsPersonAddress(e)))
            .ToList();

        if (candidates.Count == 0)
        {
            Console.WriteLine("No machine-generated contacts found.");
            return ExitCode.Success;
        }

        Console.WriteLine(
            $"{candidates.Count} contact(s) whose every email is machine-generated"
            + (apply ? ":" : " - re-run with --yes to remove:"));

        var removed = 0;

        foreach (var person in candidates)
        {
            var result = apply ? editor.DeleteIfEmpty(person.Id) : null;

            var status = result is null
                ? "would remove"
                : result.IsOk
                    ? "removed"
                    : "kept";

            if (result is not null && result.IsOk)
            {
                removed++;
            }

            Console.WriteLine($"  {status,-12} {person.Name}");
            Console.WriteLine($"               {string.Join(", ", person.Emails)}");

            if (result is not null && !result.IsOk)
            {
                Console.WriteLine($"               {result.Message}");
            }
        }

        if (apply)
        {
            Console.WriteLine();
            Console.WriteLine($"Removed {removed}. Moved to data/people/.trash/ so this can be undone.");
        }

        return ExitCode.Success;
    }

    internal static int Gender(string[] args)
    {
        // sg gender <person|suggest|inferred|unrecorded> [<gender>] [--clear]
        var action = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));

        if (action is "suggest" or "preview" or "infer")
        {
            return Infer(args, apply: args.Contains("--yes", StringComparer.Ordinal), clear: false);
        }

        if (action is "inferred")
        {
            return Infer(args, apply: args.Contains("--yes", StringComparer.Ordinal), clear: args.Contains("--clear", StringComparer.Ordinal));
        }

        var clear = args.Contains("--clear", StringComparer.Ordinal);
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");
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
        var gender = positional.Count > 1 ? string.Join(' ', positional.Skip(1)) : null;

        if (personArg is null)
        {
            Console.Error.WriteLine("sg: usage: sg gender <person|id> [<gender>] [--clear]");
            return ExitCode.Usage;
        }

        var located = Workspace.Locate(workspaceArg);

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var store = new PersonStore(located.ValueOrThrow());

        if (gender is null && !clear)
        {
            var found = PersonCommands.FindPerson(store, personArg);

            if (!found.IsOk)
            {
                ConsoleRenderer.WriteFailure(found);
                return ExitCode.MapExitCode(found.Code);
            }

            var person = found.ValueOrThrow();

            Console.WriteLine(person.Gender is null
                ? $"{person.Name}: no gender recorded."
                : $"{person.Name}: {person.Gender}");

            return ExitCode.Success;
        }

        return WithPerson(workspaceArg, personArg, "sg gender", (editor, edStore, person) =>
        {
            var result = editor.SetGender(person.Id, clear ? null : gender);

            if (!result.IsOk)
            {
                ConsoleRenderer.WriteFailure(result);
                return ExitCode.MapExitCode(result.Code);
            }

            Console.WriteLine(clear
                ? $"Cleared the gender for {person.Name}."
                : $"{person.Name}: {gender}");

            return ExitCode.Success;
        });
    }

    /// <summary>
    /// Fills in genders from names, or reports what it would do.
    /// </summary>
    /// <remarks>
    /// Dry run by default. These values are guesses written into the same field a recorded
    /// gender uses, so a bulk write without a preview would be the one change the user cannot
    /// inspect beforehand. Every value it writes is marked inferred, so it can be reverted.
    /// </remarks>
    private static int Infer(string[] args, bool apply, bool clear)
    {
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");
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

        var people = loaded.ValueOrThrow();
        var editor = new PersonEditor(store);

        if (clear)
        {
            var inferred = people.Where(p => p.GenderWasInferred).ToList();

            if (inferred.Count == 0)
            {
                Console.WriteLine("No inferred genders to clear.");
                return ExitCode.Success;
            }

            if (!apply)
            {
                Console.WriteLine($"{inferred.Count} inferred gender(s) would be cleared - re-run with --yes:");
                foreach (var p in inferred.Take(20))
                {
                    Console.WriteLine($"  {p.Name} ({p.Gender})");
                }

                return ExitCode.Success;
            }

            var cleared = editor.ClearInferredGenders(people);

            if (!cleared.IsOk)
            {
                ConsoleRenderer.WriteFailure(cleared);
                return ExitCode.MapExitCode(cleared.Code);
            }

            Console.WriteLine($"Cleared {cleared.ValueOrThrow()} inferred gender(s).");
            return ExitCode.Success;
        }

        // Only fill blanks: an existing gender, recorded or inferred, is never overwritten.
        var candidates = people
            .Where(p => string.IsNullOrWhiteSpace(p.Gender))
            .Select(p => (Person: p, Guess: GenderGuess.Guess(p.Name)))
            .Where(x => x.Guess is not null)
            .ToList();

        var unguessable = people.Count(p => string.IsNullOrWhiteSpace(p.Gender)) - candidates.Count;

        if (candidates.Count == 0)
        {
            Console.WriteLine("Nothing to infer.");
            return ExitCode.Success;
        }

        Console.WriteLine(
            $"{candidates.Count} of {people.Count} contacts have a guessable name"
            + (apply ? ":" : " (dry run - re-run with --yes to apply):"));

        foreach (var (person, guess) in candidates.Take(25))
        {
            Console.WriteLine($"  {person.Name,-36} {guess!.Gender}   (from '{guess.Source}')");
        }

        if (candidates.Count > 25)
        {
            Console.WriteLine($"  … and {candidates.Count - 25} more");
        }

        Console.WriteLine();
        Console.WriteLine($"  {unguessable} left alone: ambiguous or unrecognised names.");

        if (!apply)
        {
            return ExitCode.Success;
        }

        var written = 0;

        foreach (var (person, guess) in candidates)
        {
            var result = editor.SetInferredGender(person.Id, guess!.Gender);

            if (result.IsOk)
            {
                written++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Wrote {written} inferred gender(s). Undo with: sg gender inferred --clear --yes");

        return ExitCode.Success;
    }

    internal static int Met(string[] args)
    {
        // sg met <person> [<how>] [--clear]
        var clear = args.Contains("--clear", StringComparer.Ordinal);
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");

        // Collect positionals while skipping flag values, so --workspace's argument is never
        // mistaken for the connection text.
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

        // Everything after the person is the value, so "the climbing gym" needs no quoting.
        var value = positional.Count > 1 ? string.Join(' ', positional.Skip(1)) : null;

        if (personArg is null)
        {
            Console.Error.WriteLine("sg: usage: sg met <person|id> [<how you know them>] [--clear]");
            return ExitCode.Usage;
        }

        var located = Workspace.Locate(workspaceArg);

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var store = new PersonStore(located.ValueOrThrow());

        if (value is null && !clear)
        {
            var found = PersonCommands.FindPerson(store, personArg);

            if (!found.IsOk)
            {
                ConsoleRenderer.WriteFailure(found);
                return ExitCode.MapExitCode(found.Code);
            }

            var person = found.ValueOrThrow();

            Console.WriteLine(person.HowWeMet is null
                ? $"{person.Name}: no connection recorded."
                : $"{person.Name}: met via {ConnectionKinds.Display(person.HowWeMet)}.");

            var everyone = store.ListAll(out _);

            if (everyone.IsOk)
            {
                var inUse = ConnectionKinds.InUse(everyone.ValueOrThrow());

                if (inUse.Count > 0)
                {
                    Console.WriteLine(
                        $"  in use: {string.Join(", ", inUse.Select(ConnectionKinds.Display))}");
                }
            }

            return ExitCode.Success;
        }

        return WithPerson(workspaceArg, personArg, "sg met", (editor, edStore, person) =>
        {
            if (!clear && value is not null)
            {
                var everyone = edStore.ListAll(out _);
                var inUse = everyone.IsOk ? ConnectionKinds.InUse(everyone.ValueOrThrow()) : [];

                if (ConnectionKinds.SuggestExisting(value, inUse) is { } suggestion)
                {
                    Console.Error.WriteLine(
                        $"sg: warning: '{value}' looks like '{ConnectionKinds.Display(suggestion)}', "
                        + "which is already in use.");
                }
            }

            var result = editor.SetHowWeMet(person.Id, clear ? null : value);

            if (!result.IsOk)
            {
                ConsoleRenderer.WriteFailure(result);
                return ExitCode.MapExitCode(result.Code);
            }

            Console.WriteLine(clear
                ? $"Cleared the connection for {person.Name}."
                : $"{person.Name}: met via {ConnectionKinds.Display(value)}.");

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
