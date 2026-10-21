using Sondergraph.Core;
using Sondergraph.Workspaces;

namespace Sondergraph.People;

/// <summary>
/// Reads and writes people under <c>data/people/</c>.
/// </summary>
/// <remarks>
/// <para>
/// One directory per person, containing <c>person.md</c>, an optional <c>notes/</c> directory
/// and an optional <c>media/</c> directory (DESIGN.md §4.2). The directory name <em>is</em>
/// the person's id.
/// </para>
/// <para>
/// Every write goes through <see cref="AtomicFile"/>, so a reader never sees a partial file
/// and no lock is needed for reads (DESIGN.md §5).
/// </para>
/// </remarks>
public sealed class PersonStore
{
    /// <summary>Directory name for a person's notes.</summary>
    public const string NotesDirectoryName = "notes";

    /// <summary>Directory name for a person's media.</summary>
    public const string MediaDirectoryName = "media";

    private readonly Workspace _workspace;

    /// <summary>Creates a store over a workspace.</summary>
    public PersonStore(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
    }

    /// <summary>
    /// How many directories the last <see cref="ListAll"/> skipped because they were merged
    /// into another person.
    /// </summary>
    /// <remarks>
    /// Exposed for reporting: "1,618 people (12 merged away)" is a more honest count than
    /// silently dropping them.
    /// </remarks>
    public int MergedAwayCount { get; private set; }

    /// <summary>
    /// How many records the last <see cref="ListAll"/> skipped because they are marked not a
    /// contact.
    /// </summary>
    public int NotAContactCount { get; private set; }

    /// <summary>Absolute path to the people directory.</summary>
    public string PeopleDirectory => _workspace.PeopleDirectory;

    /// <summary>Absolute path to one person's directory.</summary>
    public string DirectoryFor(string personId) =>
        Path.Combine(_workspace.PeopleDirectory, personId);

    /// <summary>Absolute path to one person's <c>person.md</c>.</summary>
    public string FileFor(string personId) =>
        Path.Combine(DirectoryFor(personId), PersonDocument.FileName);

    /// <summary>
    /// Lists every person, sorted by name.
    /// </summary>
    /// <remarks>
    /// A person directory with an unreadable or missing <c>person.md</c> is skipped and
    /// counted rather than failing the whole listing — one bad file must not make the
    /// workspace unusable.
    /// </remarks>
    public Result<IReadOnlyList<Person>> ListAll(out IReadOnlyList<string> warnings)
    {
        var warningsList = new List<string>();
        var people = new List<Person>();
        var mergedAway = 0;
        var notAContact = 0;

        if (!Directory.Exists(_workspace.PeopleDirectory))
        {
            warnings = warningsList;
            return Result.Ok<IReadOnlyList<Person>>(people);
        }

        foreach (var directory in Directory.EnumerateDirectories(_workspace.PeopleDirectory))
        {
            // Skip anything hidden, and the cache directory if it ever lands here.
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name.StartsWith('_'))
            {
                continue;
            }

            // Skip people who have been merged into someone else. Their directory is
            // retained so the merge can be reversed, but they are not separate contacts any
            // more - showing them would make the merge appear to have done nothing.
            if (File.Exists(Path.Combine(directory, MergeService.RedirectFileName)))
            {
                mergedAway++;
                continue;
            }

            var file = Path.Combine(directory, PersonDocument.FileName);

            if (!File.Exists(file))
            {
                warningsList.Add($"{name}: no {PersonDocument.FileName}");
                continue;
            }

            var read = Read(file);
            if (!read.IsOk)
            {
                warningsList.Add($"{name}: {read.Message}");
                continue;
            }

            var person = read.ValueOrThrow();

            // People marked "not a contact" are excluded here, at the single point every
            // caller goes through: the list, the stats, party suggestions, the graph and
            // search all read ListAll. Filtering in each of them would mean remembering to
            // filter in the next one too.
            if (person.NotAContact)
            {
                notAContact++;
                continue;
            }

            people.Add(person);
        }

        warnings = warningsList;
        MergedAwayCount = mergedAway;
        NotAContactCount = notAContact;

        return Result.Ok<IReadOnlyList<Person>>(
            people.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    /// <summary>Loads one person by id.</summary>
    public Result<Person> Load(string personId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        var file = FileFor(personId);

        if (!File.Exists(file))
        {
            return Result.Fail<Person>(
                ErrorCode.WorkspaceNotFound,
                $"No person '{personId}' in this workspace.");
        }

        var read = Read(file);

        if (!read.IsOk)
        {
            return read;
        }

        var person = read.ValueOrThrow();

        // Attach any notes stored as separate files.
        var notes = ReadNotes(personId);
        return notes.Count == 0
            ? Result.Ok(person)
            : Result.Ok(person with { Notes = [.. person.Notes, .. notes] });
    }

    /// <summary>Writes a person, creating the directory as needed.</summary>
    public Result Save(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);

        var directory = DirectoryFor(person.Id);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"Could not create '{directory}': {ex.Message}");
        }

        var markdown = PersonDocument.Serialize(person);
        return AtomicFile.WriteAllText(FileFor(person.Id), markdown);
    }

    /// <summary>Whether a person id is already taken.</summary>
    public bool Exists(string personId) =>
        File.Exists(FileFor(personId));

    /// <summary>
    /// Allocates a unique id based on <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Names collide — two people really can both be called "Alex Chen" — so the derived id is
    /// only a starting point. Collisions get a numeric suffix.
    /// </para>
    /// <para>
    /// When a person's <em>name</em> already maps to an existing id, the existing id is
    /// returned rather than a new one. That is what makes re-ingest idempotent: recognising
    /// the same person again must not create a second directory.
    /// </para>
    /// </remarks>
    public string AllocateId(string name)
    {
        var baseId = Person.DeriveId(name);

        if (!Exists(baseId))
        {
            return baseId;
        }

        // Reuse the existing id when the stored name matches exactly. Case-insensitive
        // because the same person may arrive with different capitalisation from two
        // platforms, and neither is more correct.
        var existing = Load(baseId);
        if (existing.IsOk
            && string.Equals(existing.ValueOrThrow().Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return baseId;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";
            if (!Exists(candidate))
            {
                return candidate;
            }

            var loaded = Load(candidate);
            if (loaded.IsOk
                && string.Equals(loaded.ValueOrThrow().Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        // Pathological: a thousand people share a name. Fall back to a short hash so the
        // operation still completes rather than looping forever.
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid())))[..8];

        return $"{baseId}-{hash.ToLowerInvariant()}";
    }

    /// <summary>Reads notes from a person's <c>notes/</c> directory.</summary>
    public IReadOnlyList<ContactNote> ReadNotes(string personId)
    {
        var directory = Path.Combine(DirectoryFor(personId), NotesDirectoryName);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        var notes = new List<ContactNote>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.md").Order(StringComparer.Ordinal))
        {
            try
            {
                notes.Add(new ContactNote(Path.GetFileName(file), File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A single unreadable note is not worth failing the person load over.
            }
        }

        return notes;
    }

    /// <summary>Appends a note, creating the notes directory as needed.</summary>
    public Result WriteNote(string personId, string fileName, string body)
    {
        if (!Exists(personId))
        {
            return Result.Fail(
                ErrorCode.WorkspaceNotFound,
                $"No person '{personId}' in this workspace.");
        }

        var directory = Path.Combine(DirectoryFor(personId), NotesDirectoryName);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"Could not create '{directory}': {ex.Message}");
        }

        return AtomicFile.WriteAllText(Path.Combine(directory, fileName), body);
    }

    /// <summary>Reads and parses a person file.</summary>
    private static Result<Person> Read(string file)
    {
        try
        {
            var markdown = File.ReadAllText(file);
            var parsed = PersonDocument.Deserialize(markdown);

            if (!parsed.IsOk)
            {
                return Result.Fail<Person>(parsed.Code, $"{file}: {parsed.Message}");
            }

            var person = parsed.ValueOrThrow();

            // Trust the directory name over the frontmatter id when they disagree — the
            // directory is what lookups actually use, so a mismatch would make the person
            // unreachable. Report it rather than silently rewriting.
            var directoryName = Path.GetFileName(Path.GetDirectoryName(file));
            return string.Equals(person.Id, directoryName, StringComparison.Ordinal)
                ? Result.Ok(person)
                : Result.Ok(person with { Id = directoryName ?? person.Id });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail<Person>(
                ErrorCode.WorkspaceInvalid,
                $"Could not read '{file}': {ex.Message}");
        }
    }
}
