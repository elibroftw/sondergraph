using System.Globalization;
using System.Text;
using Sondergraph.Core;

namespace Sondergraph.People;

/// <summary>
/// Creates, updates and deletes contact notes.
/// </summary>
/// <remarks>
/// <para>
/// Notes are plain markdown files under <c>data/people/&lt;id&gt;/notes/</c>. That choice is
/// deliberate and load-bearing (DESIGN.md §4.2, §6.1):
/// </para>
/// <list type="bullet">
/// <item>They are readable and editable in any editor, and survive deleting this app.</item>
/// <item>Feature #3 wants to integrate with Obsidian, Logseq and Notesnook — all of which
/// work on markdown files on disk. A database would need an export layer before any
/// integration could exist.</item>
/// <item>They diff in git, so a note's history is reviewable.</item>
/// </list>
/// <para>
/// This type exists because <see cref="PersonStore.WriteNote"/> takes a raw filename and
/// cannot replace or delete. Callers should never have to invent a filename: the file name
/// is an implementation detail of storage, and a user thinks in terms of "my note about
/// them", not "2026-10-20-143022.md".
/// </para>
/// </remarks>
public sealed class NoteService
{
    /// <summary>File name used for the single free-text note about a person.</summary>
    public const string MainNoteFileName = "notes.md";

    private readonly PersonStore _store;

    /// <summary>Creates a note service over a store.</summary>
    public NoteService(PersonStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// The person's main note, or an empty string when they have none.
    /// </summary>
    /// <remarks>
    /// Treating "no note" as an empty string rather than an error keeps the common case
    /// simple: an editor opens with a blank document instead of having to distinguish
    /// "missing" from "empty".
    /// </remarks>
    public Result<string> GetMainNote(string personId)
    {
        if (!_store.Exists(personId))
        {
            return Result.Fail<string>(
                ErrorCode.WorkspaceNotFound,
                $"No person '{personId}' in this workspace.");
        }

        // Read directly rather than through ReadNotes, so an unusually large notes directory
        // does not cost a full scan to show one file.
        var path = Path.Combine(_store.DirectoryFor(personId), PersonStore.NotesDirectoryName, MainNoteFileName);

        var read = Sondergraph.Workspaces.AtomicFile.ReadAllTextOrNull(path);

        return read.IsOk
            ? Result.Ok(read.ValueOrThrow() ?? string.Empty)
            : Result.Fail<string>(read.Code, read.Message, read.Hint);
    }

    /// <summary>
    /// Writes the person's main note, replacing any existing content.
    /// </summary>
    /// <param name="personId">Person to annotate.</param>
    /// <param name="body">Markdown body. An empty or whitespace-only body deletes the note.</param>
    /// <remarks>
    /// An empty body deletes the file rather than leaving an empty one. A zero-byte note would
    /// show up as "has a note" in lists and counts, which is misleading — nobody means to
    /// record "I have nothing to say about this person".
    /// </remarks>
    public Result SaveMainNote(string personId, string body)
    {
        ArgumentNullException.ThrowIfNull(personId);
        ArgumentNullException.ThrowIfNull(body);

        if (!_store.Exists(personId))
        {
            return Result.Fail(
                ErrorCode.WorkspaceNotFound,
                $"No person '{personId}' in this workspace.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return DeleteMainNote(personId);
        }

        // Normalise line endings so the same content typed on different platforms produces
        // the same file, keeping git diffs stable.
        var normalised = body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

        return _store.WriteNote(personId, MainNoteFileName, normalised);
    }

    /// <summary>Deletes the person's main note if it exists.</summary>
    public Result DeleteMainNote(string personId)
    {
        ArgumentNullException.ThrowIfNull(personId);

        if (!_store.Exists(personId))
        {
            return Result.Fail(
                ErrorCode.WorkspaceNotFound,
                $"No person '{personId}' in this workspace.");
        }

        var path = Path.Combine(_store.DirectoryFor(personId), PersonStore.NotesDirectoryName, MainNoteFileName);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"Could not delete the note: {ex.Message}");
        }
    }

    /// <summary>
    /// Appends a dated entry to the person's log note, creating it as needed.
    /// </summary>
    /// <param name="personId">Person to annotate.</param>
    /// <param name="body">Entry text.</param>
    /// <param name="timestamp">When the entry was made. Defaults to now.</param>
    /// <remarks>
    /// <para>
    /// A second kind of note, because the two use cases are genuinely different. The main
    /// note is a document you rewrite ("who they are, what matters"). A log entry is a
    /// timestamped observation you add to ("met for coffee", "started a new job").
    /// </para>
    /// <para>
    /// Entries append rather than rewrite, so recording something is never destructive. That
    /// is the right default for a note-taking action: a user adding a quick line should not
    /// have to trust that nothing else is being replaced.
    /// </para>
    /// </remarks>
    public Result AppendLogEntry(string personId, string body, DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(personId);

        if (string.IsNullOrWhiteSpace(body))
        {
            return Result.Fail(ErrorCode.Unknown, "An entry cannot be empty.");
        }

        var existing = GetMainNote(personId);
        if (!existing.IsOk)
        {
            return Result.Fail(existing.Code, existing.Message, existing.Hint);
        }

        var when = (timestamp ?? DateTimeOffset.Now).ToLocalTime();
        var text = existing.ValueOrThrow().TrimEnd();

        // A blank line between blocks, so the heading renders as a heading in markdown
        // rather than being absorbed into the previous paragraph. SaveMainNote trims the
        // trailing newline, so the separator has to be added here on each append.
        var separator = text.Length > 0 ? "\n\n" : string.Empty;

        var combined = new StringBuilder()
            .Append(text)
            .Append(separator)
            .Append("## ").Append(when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n')
            .Append('\n')
            .Append(body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim())
            .ToString();

        return SaveMainNote(personId, combined);
    }

    /// <summary>
    /// Summarises the first line of a note, for lists.
    /// </summary>
    /// <remarks>
    /// Markdown headings and blank lines are skipped so the preview is actual content rather
    /// than "# Notes". Long lines are truncated, because a list row cannot show a paragraph.
    /// </remarks>
    public static string Summarise(string? markdown, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('#').Trim();

            if (line.Length == 0)
            {
                continue;
            }

            return line.Length <= maxLength
                ? line
                : line[..maxLength].TrimEnd() + "…";
        }

        return string.Empty;
    }
}
