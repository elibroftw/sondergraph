using System.Globalization;
using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.Cli;

/// <summary>
/// Implements <c>sg list</c>, <c>sg show</c> and <c>sg stats</c>.
/// </summary>
/// <remarks>
/// Read-only, so these take no lock (DESIGN.md §5) — they rely on atomic writes meaning a
/// reader never sees a partial file. That matters: a lock would make browsing unavailable
/// whenever a long ingest is running.
/// </remarks>
internal static class PersonCommands
{
    internal static int List(string[] args)
    {
        var workspace = Resolve(args, minimumArgs: 1, out var error);

        if (workspace is null)
        {
            return error;
        }

        var store = new PersonStore(workspace);
        var loaded = store.ListAll(out var warnings);

        if (!loaded.IsOk)
        {
            ConsoleRenderer.WriteFailure(loaded);
            return ExitCode.MapExitCode(loaded.Code);
        }

        var people = loaded.ValueOrThrow();
        var filter = FilterArgument(args);

        if (filter is not null)
        {
            people = people
                .Where(p => p.AllIdentifiers().Any(i =>
                    i.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || Person.DigitsOnly(i).Contains(Person.DigitsOnly(filter), StringComparison.Ordinal)))
                .ToList();
        }

        if (people.Count == 0)
        {
            Console.WriteLine(filter is null
                ? "No people yet. Import an export with 'sg ingest parse <path>'."
                : $"No one matches '{filter}'.");
            return ExitCode.Success;
        }

        // Column widths computed from the data so the output stays aligned regardless of
        // content, rather than a hard-coded guess that breaks on the first long name.
        var idWidth = Math.Min(28, people.Max(p => p.Id.Length));
        var nameWidth = Math.Min(32, people.Max(p => DisplayWidth(p.Name)));

        foreach (var person in people)
        {
            var summary = PersonDocument.Describe(person);

            Console.WriteLine(
                $"{Pad(person.Id, idWidth)}  {Pad(person.Name, nameWidth)}  {summary}");
        }

        Console.WriteLine();
        Console.WriteLine(PersonDocument.People(people.Count)
            + (filter is null ? string.Empty : $" matching '{filter}'"));

        if (warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{warnings.Count} unreadable record(s) were skipped:");
            foreach (var warning in warnings.Take(3))
            {
                Console.WriteLine($"  {warning}");
            }
        }

        return ExitCode.Success;
    }

    internal static int Show(string[] args)
    {
        var workspace = Resolve(args, minimumArgs: 2, out var error);

        if (workspace is null)
        {
            return error;
        }

        var target = FilterArgument(args);

        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("sg: 'show' needs a person id or name.");
            return ExitCode.Usage;
        }

        var store = new PersonStore(workspace);
        var lookup = FindPerson(store, target);

        if (!lookup.IsOk)
        {
            ConsoleRenderer.WriteFailure(lookup);
            return ExitCode.MapExitCode(lookup.Code);
        }

        var person = lookup.ValueOrThrow();

        Console.WriteLine(person.Name);

        var details = PersonDocument.Describe(person);
        if (details.Length > 0)
        {
            Console.WriteLine(details);
        }

        // Only identifiers that are not already shown below. AllIdentifiers includes the
        // name, every phone in two forms, and every email, so printing it wholesale just
        // repeats the sections that follow.
        var alreadyShown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        alreadyShown.Add(person.Name);
        alreadyShown.UnionWith(person.Phones);
        alreadyShown.UnionWith(person.Phones.Select(Person.DigitsOnly));
        alreadyShown.UnionWith(person.Emails);
        alreadyShown.UnionWith(person.Aliases);

        WriteSection(
            "Other identifiers",
            person.AllIdentifiers().Where(i => !alreadyShown.Contains(i)).Distinct(StringComparer.OrdinalIgnoreCase));

        WriteList("Phones", person.Phones);
        WriteList("Emails", person.Emails);
        WriteList("Aliases", person.Aliases);
        WriteList("Tags", person.Tags);

        if (person.Locations.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Locations:");

            foreach (var location in person.Locations)
            {
                var note = string.IsNullOrWhiteSpace(location.Note) ? string.Empty : $"  ({location.Note})";
                Console.WriteLine($"  {location.Place}  [{PersonDocument.LocationToWire(location.Kind)}]{note}");
            }
        }

        if (person.Accounts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Accounts:");

            foreach (var account in person.Accounts)
            {
                var url = string.IsNullOrWhiteSpace(account.ProfileUrl) ? string.Empty : $"  {account.ProfileUrl}";
                Console.WriteLine($"  {account.Platform,-12} {account.Handle}{url}");
            }
        }

        if (person.Notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Notes ({person.Notes.Count}):");

            foreach (var note in person.Notes)
            {
                Console.WriteLine($"  {note.FileName}");
            }
        }

        WriteList("Sources", person.Sources);

        Console.WriteLine();
        Console.WriteLine($"File: {store.FileFor(person.Id)}");

        return ExitCode.Success;
    }

    internal static int Stats(string[] args)
    {
        var workspace = Resolve(args, minimumArgs: 1, out var error);

        if (workspace is null)
        {
            return error;
        }

        var store = new PersonStore(workspace);
        var loaded = store.ListAll(out _);

        if (!loaded.IsOk)
        {
            ConsoleRenderer.WriteFailure(loaded);
            return ExitCode.MapExitCode(loaded.Code);
        }

        var people = loaded.ValueOrThrow();

        Console.WriteLine("Contact graph");
        Console.WriteLine();
        Console.WriteLine($"  people:        {people.Count}");
        Console.WriteLine($"  with phone:    {people.Count(p => p.Phones.Count > 0)}");
        Console.WriteLine($"  with email:    {people.Count(p => p.Emails.Count > 0)}");
        Console.WriteLine($"  with location: {people.Count(p => p.Locations.Count > 0)}");
        Console.WriteLine($"  with notes:    {people.Count(p => p.Notes.Count > 0)}");

        var withAccounts = people.Where(p => p.Accounts.Count > 0).ToList();
        Console.WriteLine($"  with accounts: {withAccounts.Count}");

        if (withAccounts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Accounts per platform:");

            foreach (var group in withAccounts
                .SelectMany(p => p.Accounts)
                .GroupBy(a => a.Platform, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"  {group.Key,-14} {group.Count()}");
            }
        }

        if (people.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Sources:");

            foreach (var group in people
                .SelectMany(p => p.Sources)
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"  {group.Key,-14} {group.Count()}");
            }

            Console.WriteLine();
            Console.WriteLine("Tiers:");

            foreach (var group in people
                .GroupBy(p => p.Tier)
                .OrderBy(g => g.Key))
            {
                Console.WriteLine($"  {PersonDocument.DescribeTier(group.Key),-14} {group.Count()}");
            }

            var multiSource = people.Count(p => p.Sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
            Console.WriteLine();
            Console.WriteLine($"  confirmed from 2+ sources: {multiSource}");
        }

        return ExitCode.Success;
    }

    /// <summary>
    /// Finds a person by exact id, then by exact name, then by unambiguous substring.
    /// </summary>
    /// <remarks>
    /// The escalation is deliberate: an ambiguous match is reported as ambiguous rather than
    /// resolved arbitrarily, because silently picking one of two people named "Alex" is how a
    /// user edits the wrong record without noticing.
    /// </remarks>
    internal static Result<Person> FindPerson(PersonStore store, string target)
    {
        var direct = store.Load(target);

        if (direct.IsOk)
        {
            return direct;
        }

        var loaded = store.ListAll(out _);

        if (!loaded.IsOk)
        {
            return Result.Fail<Person>(loaded.Code, loaded.Message, loaded.Hint);
        }

        var people = loaded.ValueOrThrow();

        var exact = people
            .Where(p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exact.Count == 1)
        {
            return Result.Ok(exact[0]);
        }

        if (exact.Count > 1)
        {
            return Result.Fail<Person>(
                ErrorCode.ConcurrentModification,
                $"{exact.Count} people are named '{target}'. Use an id instead:",
                string.Join(", ", exact.Select(p => p.Id)));
        }

        var matches = people
            .Where(p => p.AllIdentifiers().Any(i =>
                i.Contains(target, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return matches.Count switch
        {
            0 => Result.Fail<Person>(
                ErrorCode.WorkspaceNotFound,
                $"No person matches '{target}'."),
            1 => Result.Ok(matches[0]),
            _ => Result.Fail<Person>(
                ErrorCode.ConcurrentModification,
                $"{matches.Count} people match '{target}'. Be more specific:",
                string.Join(", ", matches.Take(8).Select(p => p.Id))),
        };
    }

    private static void WriteSection(string title, IEnumerable<string> values)
    {
        var list = values.ToList();

        if (list.Count > 0)
        {
            WriteList(title, list);
        }
    }

    private static void WriteList(string title, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{title}:");

        foreach (var value in values)
        {
            Console.WriteLine($"  {value}");
        }
    }

    /// <summary>
    /// Locates the workspace, reporting a failure as an exit code.
    /// </summary>
    private static Workspace? Resolve(string[] args, int minimumArgs, out int error)
    {
        var path = args.Length > minimumArgs ? args[^1] : null;

        if (path is not null && path.StartsWith('-'))
        {
            path = null;
        }

        var located = Workspace.Locate(path);

        if (located.IsOk)
        {
            error = ExitCode.Success;
            return located.ValueOrThrow();
        }

        ConsoleRenderer.WriteFailure(located);
        error = ExitCode.MapExitCode(located.Code);
        return null;
    }

    /// <summary>The positional argument after the subcommand, if any.</summary>
    private static string? FilterArgument(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-'))
            {
                return args[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Approximate display width, counting East Asian wide characters as two columns.
    /// </summary>
    /// <remarks>
    /// Without this, a column of names containing CJK or emoji drifts out of alignment.
    /// Rune-based rather than char-based so surrogate pairs count once.
    /// </remarks>
    private static int DisplayWidth(string text)
    {
        var width = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            width += rune.Value is >= 0x1100 and (
                <= 0x115F or
                >= 0x2E80 and <= 0xA4CF or
                >= 0xAC00 and <= 0xD7A3 or
                >= 0xF900 and <= 0xFAFF or
                >= 0xFE30 and <= 0xFE6F or
                >= 0xFF00 and <= 0xFF60 or
                >= 0xFFE0 and <= 0xFFE6 or
                >= 0x1F300 and <= 0x1F64F or
                >= 0x1F900 and <= 0x1F9FF)
                ? 2
                : 1;
        }

        return width;
    }

    /// <summary>Pads to a display width, truncating with an ellipsis when too long.</summary>
    private static string Pad(string text, int width)
    {
        var display = DisplayWidth(text);

        if (display <= width)
        {
            return text + new string(' ', width - display);
        }

        // Truncate on a rune boundary so a multi-byte character is never cut in half.
        var builder = new System.Text.StringBuilder();
        var used = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = DisplayWidth(rune.ToString());

            if (used + runeWidth > width - 1)
            {
                break;
            }

            builder.Append(rune);
            used += runeWidth;
        }

        builder.Append('…');
        return builder.ToString() + new string(' ', Math.Max(0, width - used - 1));
    }
}
