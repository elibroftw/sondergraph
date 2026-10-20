using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.People.Tests;

/// <summary>
/// Tests for contact notes.
/// </summary>
/// <remarks>
/// Notes are the most user-authored data in the whole system, so the risks are different from
/// the ingest code: losing an edit, or silently replacing something a person wrote, matters
/// more than throughput. These tests are written around those risks.
/// </remarks>
public sealed class NoteServiceTests : IDisposable
{
    private readonly string _root;
    private readonly Workspace _workspace;
    private readonly PersonStore _store;
    private readonly NoteService _notes;

    public NoteServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sg-notes-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(_root);

        var created = Workspace.Create(_root);
        Assert.True(created.IsOk, created.Message);
        _workspace = created.ValueOrThrow();

        _store = new PersonStore(_workspace);
        _notes = new NoteService(_store);

        var saved = _store.Save(new Person
        {
            Id = "example-person",
            Name = "Example Person",
            CreatedAt = DateOnly.FromDateTime(DateTime.UtcNow),
        });

        Assert.True(saved.IsOk, saved.Message);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void Reads_empty_when_no_note_exists()
    {
        // "No note" is an empty string, not an error: an editor opens blank rather than
        // having to distinguish missing from empty.
        var note = _notes.GetMainNote("example-person");

        Assert.True(note.IsOk, note.Message);
        Assert.Equal(string.Empty, note.ValueOrThrow());
    }

    [Fact]
    public void Writes_and_reads_back_a_note()
    {
        var saved = _notes.SaveMainNote("example-person", "Works on distributed systems.");
        Assert.True(saved.IsOk, saved.Message);

        var read = _notes.GetMainNote("example-person");

        Assert.Equal("Works on distributed systems.\n", read.ValueOrThrow());
    }

    [Fact]
    public void Writes_the_note_as_plain_markdown_on_disk()
    {
        // The whole point of notes being files (DESIGN.md §4.2): they must be readable and
        // editable outside this app, and readable by Obsidian or Logseq.
        _notes.SaveMainNote("example-person", "# Heading\n\nBody text.");

        var path = Path.Combine(
            _store.DirectoryFor("example-person"),
            PersonStore.NotesDirectoryName,
            NoteService.MainNoteFileName);

        Assert.True(File.Exists(path), $"expected a note file at {path}");
        Assert.Contains("Body text.", File.ReadAllText(path));
    }

    [Fact]
    public void Replaces_existing_content_when_saving()
    {
        _notes.SaveMainNote("example-person", "First version.");
        _notes.SaveMainNote("example-person", "Second version.");

        var body = _notes.GetMainNote("example-person").ValueOrThrow();

        Assert.Contains("Second version.", body);
        Assert.DoesNotContain("First version.", body);
    }

    [Fact]
    public void Clearing_a_note_deletes_the_file()
    {
        _notes.SaveMainNote("example-person", "Something.");
        var cleared = _notes.SaveMainNote("example-person", "   ");

        Assert.True(cleared.IsOk, cleared.Message);
        Assert.Equal(string.Empty, _notes.GetMainNote("example-person").ValueOrThrow());

        // An empty file left behind would count as "has a note" in lists, which is misleading.
        var path = Path.Combine(
            _store.DirectoryFor("example-person"),
            PersonStore.NotesDirectoryName,
            NoteService.MainNoteFileName);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Normalises_line_endings()
    {
        // The same content typed on Windows and Linux must produce an identical file, or git
        // shows a spurious diff and the note looks edited when it was not.
        _notes.SaveMainNote("example-person", "Line one.\r\nLine two.");

        var body = _notes.GetMainNote("example-person").ValueOrThrow();

        Assert.Equal("Line one.\nLine two.\n", body);
    }

    [Fact]
    public void Appends_a_dated_entry_without_losing_existing_text()
    {
        _notes.SaveMainNote("example-person", "Met at a conference.");
        var appended = _notes.AppendLogEntry(
            "example-person",
            "Got a new job.",
            new DateTimeOffset(2026, 10, 20, 14, 30, 0, TimeSpan.Zero));

        Assert.True(appended.IsOk, appended.Message);

        var body = _notes.GetMainNote("example-person").ValueOrThrow();

        // Appending must never be destructive: the earlier text survives.
        Assert.Contains("Met at a conference.", body);
        Assert.Contains("Got a new job.", body);
        Assert.Contains("2026-10-20", body);
    }

    [Fact]
    public void Appended_entries_are_separated_so_they_render_as_headings()
    {
        // Without a blank line before "##", markdown absorbs the heading into the previous
        // paragraph and the log stops looking like a log.
        _notes.SaveMainNote("example-person", "First note.");
        _notes.AppendLogEntry(
            "example-person",
            "An entry.",
            new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));

        var body = _notes.GetMainNote("example-person").ValueOrThrow();

        Assert.Contains("\n\n## ", body);
    }

    [Fact]
    public void Appending_to_an_absent_note_creates_it()
    {
        var appended = _notes.AppendLogEntry(
            "example-person",
            "First ever entry.",
            new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));

        Assert.True(appended.IsOk, appended.Message);

        var body = _notes.GetMainNote("example-person").ValueOrThrow();

        // No leading blank lines when there was nothing before.
        Assert.StartsWith("## ", body);
        Assert.Contains("First ever entry.", body);
    }

    [Fact]
    public void Rejects_an_empty_log_entry()
    {
        var appended = _notes.AppendLogEntry("example-person", "   ");

        Assert.False(appended.IsOk);
    }

    [Fact]
    public void Unknown_person_is_reported_not_created()
    {
        // Writing a note must never silently create a person: a typo in an id would
        // otherwise quietly invent a contact.
        var result = _notes.SaveMainNote("no-such-person", "text");

        Assert.False(result.IsOk);
        Assert.Equal(ErrorCode.WorkspaceNotFound, result.Code);
    }

    [Fact]
    public void Note_survives_a_person_reload()
    {
        // Notes live in a subdirectory, so the person loader must still find them.
        _notes.SaveMainNote("example-person", "Persisted note.");

        var reloaded = _store.Load("example-person");

        Assert.True(reloaded.IsOk, reloaded.Message);
        Assert.Contains(reloaded.ValueOrThrow().Notes, n => n.Body.Contains("Persisted note.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# Heading\n\nActual content.", "Heading")]
    [InlineData("\n\n  Body first.  \nMore.", "Body first.")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Summary_skips_headings_and_blank_lines(string? markdown, string expected)
    {
        Assert.Equal(expected, NoteService.Summarise(markdown));
    }

    [Fact]
    public void Summary_truncates_a_long_line()
    {
        var summary = NoteService.Summarise(new string('x', 200));

        Assert.True(summary.Length <= 81, $"summary was {summary.Length} chars");
        Assert.EndsWith("…", summary, StringComparison.Ordinal);
    }
}
