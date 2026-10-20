using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.Cli;

/// <summary>
/// Implements <c>sg note</c>: read, write and clear a contact's note.
/// </summary>
/// <remarks>
/// <para>
/// A writer, so it takes the workspace lock (DESIGN.md §5). Reading a note does not need
/// one, but writing does, and the two share this command.
/// </para>
/// <para>
/// Three ways to supply the text, because they suit different habits:
/// </para>
/// <list type="bullet">
/// <item><c>sg note &lt;person&gt;</c> — print the note.</item>
/// <item><c>sg note &lt;person&gt; --set "text"</c> — replace it from the shell.</item>
/// <item><c>sg note &lt;person&gt; --edit</c> — open $EDITOR, for anything longer than a
/// line. This is the important one: a note is usually a paragraph, and quoting a paragraph
/// into a shell argument is miserable.</item>
/// </list>
/// </remarks>
internal static class NoteCommands
{
    internal static int Run(string[] args)
    {
        // sg note <person> [--set <text> | --edit | --append <text> | --clear] [--workspace <path>]
        var personArg = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;
        var set = ValueAfter(args, "--set") ?? ValueAfter(args, "-s");
        var append = ValueAfter(args, "--append") ?? ValueAfter(args, "-a");
        var edit = args.Contains("--edit", StringComparer.Ordinal) || args.Contains("-e", StringComparer.Ordinal);
        var clear = args.Contains("--clear", StringComparer.Ordinal);
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");

        if (personArg is null)
        {
            Console.Error.WriteLine("sg: 'note' needs a person.");
            Console.Error.WriteLine("sg: usage: sg note <person|id> [--set <text> | --append <text> | --edit | --clear]");
            return ExitCode.Usage;
        }

        var located = Workspace.Locate(workspaceArg);

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var workspace = located.ValueOrThrow();
        var store = new PersonStore(workspace);

        // Resolve the person first, so a typo fails before asking for the lock or, worse,
        // before opening an editor on a note that cannot be saved.
        var found = PersonCommands.FindPerson(store, personArg);

        if (!found.IsOk)
        {
            ConsoleRenderer.WriteFailure(found);
            return ExitCode.MapExitCode(found.Code);
        }

        var person = found.ValueOrThrow();
        var notes = new NoteService(store);

        // Reading needs no lock; every mutation does.
        if (set is null && append is null && !edit && !clear)
        {
            var existing = notes.GetMainNote(person.Id);

            if (!existing.IsOk)
            {
                ConsoleRenderer.WriteFailure(existing);
                return ExitCode.MapExitCode(existing.Code);
            }

            var body = existing.ValueOrThrow();

            if (body.Length == 0)
            {
                Console.WriteLine($"{person.Name} has no note.");
                Console.WriteLine($"Write one with: sg note {person.Id} --edit");
                return ExitCode.Success;
            }

            // The body verbatim, so `sg note x > file.md` is a usable export.
            Console.Write(body);
            return ExitCode.Success;
        }

        using var acquired = WorkspaceLock.TryAcquire(workspace, "sg note");

        if (!acquired.IsOk)
        {
            ConsoleRenderer.WriteFailure(acquired);
            return ExitCode.MapExitCode(acquired.Code);
        }

        if (clear)
        {
            var cleared = notes.DeleteMainNote(person.Id);

            if (!cleared.IsOk)
            {
                ConsoleRenderer.WriteFailure(cleared);
                return ExitCode.MapExitCode(cleared.Code);
            }

            Console.WriteLine($"Cleared the note for {person.Name}.");
            return ExitCode.Success;
        }

        if (append is not null)
        {
            var appended = notes.AppendLogEntry(person.Id, append);

            if (!appended.IsOk)
            {
                ConsoleRenderer.WriteFailure(appended);
                return ExitCode.MapExitCode(appended.Code);
            }

            Console.WriteLine($"Added an entry to {person.Name}'s note.");
            return ExitCode.Success;
        }

        if (set is not null)
        {
            var saved = notes.SaveMainNote(person.Id, set);

            if (!saved.IsOk)
            {
                ConsoleRenderer.WriteFailure(saved);
                return ExitCode.MapExitCode(saved.Code);
            }

            Console.WriteLine($"Saved the note for {person.Name}.");
            return ExitCode.Success;
        }

        // --edit: hand the existing body to $EDITOR.
        var current = notes.GetMainNote(person.Id);

        if (!current.IsOk)
        {
            ConsoleRenderer.WriteFailure(current);
            return ExitCode.MapExitCode(current.Code);
        }

        var edited = EditInEditor(person.Name, current.ValueOrThrow());

        if (edited is null)
        {
            Console.WriteLine("No changes; the editor closed with nothing to save.");
            return ExitCode.Success;
        }

        if (edited == current.ValueOrThrow())
        {
            Console.WriteLine("No changes.");
            return ExitCode.Success;
        }

        var write = notes.SaveMainNote(person.Id, edited);

        if (!write.IsOk)
        {
            ConsoleRenderer.WriteFailure(write);
            return ExitCode.MapExitCode(write.Code);
        }

        Console.WriteLine($"Saved the note for {person.Name}.");
        return ExitCode.Success;
    }

    /// <summary>
    /// Opens the user's editor on the note text and returns the result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses <c>$VISUAL</c> then <c>$EDITOR</c>, matching what git does, so anyone with a
    /// working commit editor has a working note editor. Falls back to a small set of common
    /// editors, then to reporting the problem rather than silently discarding the text.
    /// </para>
    /// <para>
    /// The file goes in the system temp directory rather than the notes directory: an
    /// interrupted edit must not leave a half-written note behind, and the real write happens
    /// atomically afterwards.
    /// </para>
    /// </remarks>
    private static string? EditInEditor(string personName, string existing)
    {
        var editor = Environment.GetEnvironmentVariable("VISUAL")
            ?? Environment.GetEnvironmentVariable("EDITOR")
            ?? FirstAvailable("nano", "vim", "vi", "micro");

        if (editor is null)
        {
            Console.Error.WriteLine("sg: no editor found.");
            Console.Error.WriteLine("sg: set $EDITOR, or pass --set \"text\" instead.");
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"sondergraph-note-{Guid.NewGuid():n}.md");

        try
        {
            // A header naming the person, commented out so it is obviously not part of the
            // note if the user leaves it in place.
            var seed = existing.Length > 0
                ? existing
                : $"<!-- note about {personName} -->\n";

            File.WriteAllText(path, seed);

            var startInfo = new System.Diagnostics.ProcessStartInfo(editor)
            {
                UseShellExecute = false,
            };

            startInfo.ArgumentList.Add(path);

            using var process = System.Diagnostics.Process.Start(startInfo);

            if (process is null)
            {
                Console.Error.WriteLine($"sg: could not start '{editor}'.");
                return null;
            }

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"sg: '{editor}' exited with code {process.ExitCode}; note not saved.");
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"sg: could not run the editor: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing the command over.
            }
        }
    }

    private static string? FirstAvailable(params string[] candidates)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        var directories = pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var candidate in candidates)
        {
            foreach (var directory in directories)
            {
                if (File.Exists(Path.Combine(directory, candidate)))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Returns the argument following <paramref name="flag"/>, or null.</summary>
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
