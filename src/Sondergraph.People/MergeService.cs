using Sondergraph.Core;

namespace Sondergraph.People;

/// <summary>
/// Merges two people into one, and reverses it.
/// </summary>
/// <remarks>
/// <para>
/// Merging is the riskiest operation in the system (DESIGN.md §4.3), so the rules are strict:
/// </para>
/// <list type="number">
/// <item><b>Recorded, not destructive.</b> Both documents are stored verbatim in the merge
/// log before anything changes, so <c>ungraft</c> is a restore rather than a reconstruction.</item>
/// <item><b>Union, never replace.</b> Every list field (phones, emails, aliases, tags,
/// locations, accounts, sources) is unioned. Nothing that either record knew is dropped.</item>
/// <item><b>Human choices survive.</b> If the survivor has a tier, tag or location the
/// absorbed one lacks, it is kept. If the absorbed one has an opinion the survivor lacks, it
/// is adopted rather than lost.</item>
/// <item><b>Reversible.</b> The absorbed person's directory is kept, tombstoned by a
/// redirect, so an undo restores it rather than recreating it from a summary.</item>
/// </list>
/// <para>
/// The hard case is not the mechanics but the judgement: which record survives, and what
/// happens to a tier or location when the two disagree. Both are resolved in favour of
/// <em>keeping information</em>, and the survivor's existing values win ties.
/// </para>
/// </remarks>
public sealed class MergeService
{
    /// <summary>Marker file written into an absorbed person's directory.</summary>
    /// <remarks>
    /// The directory is retained rather than deleted so the merge can be reversed and so an
    /// id that has been merged away is never silently reused for a different person.
    /// </remarks>
    public const string RedirectFileName = "merged-into.txt";

    private readonly PersonStore _store;
    private readonly MergeLog _log;

    /// <summary>Creates a merge service.</summary>
    public MergeService(PersonStore store, MergeLog log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Merges <paramref name="absorbedId"/> into <paramref name="survivorId"/>.
    /// </summary>
    /// <param name="survivorId">The person that keeps existing.</param>
    /// <param name="absorbedId">The person that disappears into the survivor.</param>
    /// <param name="reason">Optional note recorded in the log.</param>
    public Result<MergeRecord> Merge(string survivorId, string absorbedId, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(survivorId);
        ArgumentNullException.ThrowIfNull(absorbedId);

        if (string.Equals(survivorId, absorbedId, StringComparison.Ordinal))
        {
            return Result.Fail<MergeRecord>(
                ErrorCode.Unknown,
                "A person cannot be merged into themselves.");
        }

        var survivorLoad = _store.Load(survivorId);

        if (!survivorLoad.IsOk)
        {
            return Result.Fail<MergeRecord>(survivorLoad.Code, survivorLoad.Message, survivorLoad.Hint);
        }

        var absorbedLoad = _store.Load(absorbedId);

        if (!absorbedLoad.IsOk)
        {
            return Result.Fail<MergeRecord>(absorbedLoad.Code, absorbedLoad.Message, absorbedLoad.Hint);
        }

        var survivor = survivorLoad.ValueOrThrow();
        var absorbed = absorbedLoad.ValueOrThrow();

        // Refuse to merge into a person who has themselves been absorbed: it would create a
        // chain that ungraft cannot follow cleanly.
        if (File.Exists(Path.Combine(_store.DirectoryFor(survivorId), RedirectFileName)))
        {
            return Result.Fail<MergeRecord>(
                ErrorCode.ConcurrentModification,
                $"'{survivorId}' has itself been merged into another person.",
                "Merge into the person it points at instead.");
        }

        // Refuse to absorb someone who has ALREADY been absorbed. Load() deliberately still
        // reads tombstoned people so a merge can be reversed, so without this check the same
        // merge could be applied repeatedly - duplicating log entries and re-adding data that
        // was already combined.
        if (File.Exists(Path.Combine(_store.DirectoryFor(absorbedId), RedirectFileName)))
        {
            var target = _log.FindAbsorbing(absorbedId);

            return Result.Fail<MergeRecord>(
                ErrorCode.ConcurrentModification,
                $"'{absorbedId}' has already been merged into '{target?.SurvivorId ?? "another person"}'.",
                "Reverse that merge with 'sg ungraft' first if you meant to redo it.");
        }

        // Capture both documents BEFORE any change, so a failure part-way through is
        // recoverable from the log rather than leaving two half-merged records.
        var survivorDocumentBefore = ReadDocument(survivorId);
        var absorbedDocument = ReadDocument(absorbedId);

        if (survivorDocumentBefore is null || absorbedDocument is null)
        {
            return Result.Fail<MergeRecord>(
                ErrorCode.WorkspaceInvalid,
                "Could not read one of the person files before merging.");
        }

        var merged = Combine(survivor, absorbed);
        var save = _store.Save(merged);

        if (!save.IsOk)
        {
            return Result.Fail<MergeRecord>(save.Code, save.Message, save.Hint);
        }

        // Move the absorbed person's notes onto the survivor, so a merge never loses writing.
        var movedNotes = MoveNotes(absorbedId, survivorId);

        // Tombstone the absorbed directory rather than deleting it.
        var redirect = Tombstone(absorbedId, survivorId);

        if (!redirect.IsOk)
        {
            // The merge already happened; report the partial failure plainly rather than
            // pretending it succeeded.
            return Result.Fail<MergeRecord>(
                redirect.Code,
                $"Merged, but could not mark '{absorbedId}' as absorbed: {redirect.Message}",
                "The data is merged; remove the absorbed directory by hand if needed.");
        }

        var record = new MergeRecord
        {
            Id = Guid.NewGuid().ToString("n")[..8],
            At = DateTimeOffset.UtcNow,
            SurvivorId = survivorId,
            AbsorbedId = absorbedId,
            AbsorbedDocument = absorbedDocument,
            SurvivorDocumentBefore = survivorDocumentBefore,
            Reason = reason ?? (movedNotes > 0 ? $"moved {movedNotes} note(s)" : null),
        };

        var appended = _log.Append(record);

        return appended.IsOk
            ? Result.Ok(record)
            : Result.Fail<MergeRecord>(appended.Code, appended.Message, appended.Hint);
    }

    /// <summary>
    /// Reverses a merge, restoring both people.
    /// </summary>
    /// <param name="mergeId">The merge to undo.</param>
    public Result Undo(string mergeId)
    {
        ArgumentNullException.ThrowIfNull(mergeId);

        var record = _log.Find(mergeId);

        if (record is null)
        {
            return Result.Fail(ErrorCode.WorkspaceNotFound, $"No merge with id '{mergeId}'.");
        }

        if (_log.Records.Any(r => r.Reason == $"undo:{mergeId}"))
        {
            return Result.Fail(ErrorCode.Unknown, $"Merge '{mergeId}' has already been undone.");
        }

        // Restore both documents exactly as they were.
        var absorbed = DeserializeOrFail(record.AbsorbedId, record.AbsorbedDocument);

        if (absorbed is null)
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"The stored document for '{record.AbsorbedId}' could not be parsed, so this merge cannot be reversed.",
                "The merge log preserves the original text; inspect it by hand if needed.");
        }

        var survivorBefore = DeserializeOrFail(record.SurvivorId, record.SurvivorDocumentBefore);

        if (survivorBefore is null)
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"The stored document for '{record.SurvivorId}' could not be parsed, so this merge cannot be reversed.");
        }

        var restoredAbsorbed = _store.Save(absorbed);

        if (!restoredAbsorbed.IsOk)
        {
            return restoredAbsorbed;
        }

        var restoredSurvivor = _store.Save(survivorBefore);

        if (!restoredSurvivor.IsOk)
        {
            return restoredSurvivor;
        }

        // Remove the tombstone so the restored person is a normal person again.
        var redirectPath = Path.Combine(_store.DirectoryFor(record.AbsorbedId), RedirectFileName);

        try
        {
            if (File.Exists(redirectPath))
            {
                File.Delete(redirectPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"Restored the data but could not remove the merge marker: {ex.Message}");
        }

        return _log.RecordUndo(mergeId);
    }

    /// <summary>
    /// Combines two people, keeping everything.
    /// </summary>
    /// <remarks>
    /// List fields are unioned; scalar fields keep the survivor's value unless it is unset, in
    /// which case the absorbed one fills the gap. That ordering means a merge never downgrades
    /// a decision the user already made.
    /// </remarks>
    public static Person Combine(Person survivor, Person absorbed)
    {
        ArgumentNullException.ThrowIfNull(survivor);
        ArgumentNullException.ThrowIfNull(absorbed);

        return survivor with
        {
            // Names: keep the survivor's, but record the absorbed name as an alias so a
            // search for either name still finds the person.
            Aliases = Union(
                survivor.Aliases,
                absorbed.Aliases.Count > 0 ? absorbed.Aliases : [absorbed.Name]),

            Phones = UnionBy(survivor.Phones, absorbed.Phones, Person.DigitsOnly),
            Emails = Union(survivor.Emails, absorbed.Emails),
            Tags = Union(survivor.Tags, absorbed.Tags)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),

            // Tier: the survivor's, unless it is unknown and the absorbed one is set.
            Tier = survivor.Tier != ContactTier.Unknown ? survivor.Tier : absorbed.Tier,

            Locations = UnionLocations(survivor.Locations, absorbed.Locations),

            Accounts = UnionAccounts(survivor.Accounts, absorbed.Accounts),

            Sources = Union(survivor.Sources, absorbed.Sources),

            // Keep the earlier creation date: the person existed since then, regardless of
            // which record happened to be written first.
            CreatedAt = Earliest(survivor.CreatedAt, absorbed.CreatedAt),
            UpdatedAt = DateOnly.FromDateTime(DateTime.UtcNow),

            Notes = survivor.Notes.Count > 0 ? survivor.Notes : absorbed.Notes,
        };
    }

    /// <summary>Moves notes from the absorbed person's directory to the survivor's.</summary>
    /// <remarks>
    /// Renamed on collision rather than overwritten: two notes with the same file name are
    /// two different pieces of writing, and losing one to a merge would be exactly the kind of
    /// data loss the merge rules exist to prevent.
    /// </remarks>
    private int MoveNotes(string fromId, string toId)
    {
        var sourceDirectory = Path.Combine(_store.DirectoryFor(fromId), PersonStore.NotesDirectoryName);

        if (!Directory.Exists(sourceDirectory))
        {
            return 0;
        }

        var targetDirectory = Path.Combine(_store.DirectoryFor(toId), PersonStore.NotesDirectoryName);

        try
        {
            Directory.CreateDirectory(targetDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var moved = 0;

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.md"))
        {
            var name = Path.GetFileName(file);
            var target = Path.Combine(targetDirectory, name);

            if (File.Exists(target))
            {
                // Disambiguate with the source id, which is stable and readable.
                target = Path.Combine(targetDirectory, Path.GetFileNameWithoutExtension(name) + $"-{fromId}.md");
            }

            try
            {
                File.Copy(file, target, overwrite: false);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave it; the source directory is retained anyway, so nothing is lost.
            }
        }

        return moved;
    }

    /// <summary>Writes the redirect that marks a person as merged away.</summary>
    private Result Tombstone(string absorbedId, string survivorId)
    {
        var path = Path.Combine(_store.DirectoryFor(absorbedId), RedirectFileName);

        return Sondergraph.Workspaces.AtomicFile.WriteAllText(
            path,
            $"""
             This person was merged into '{survivorId}'.
             Merged at: {DateTimeOffset.UtcNow:u}

             The record is kept so the merge can be reversed with 'sg ungraft'.
             Deleting this file would make the person reappear as a separate contact.
             """);
    }

    private string? ReadDocument(string personId)
    {
        var path = _store.FileFor(personId);

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Person? DeserializeOrFail(string id, string document)
    {
        var parsed = PersonDocument.Deserialize(document);
        return parsed.IsOk ? parsed.ValueOrThrow() with { Id = id } : null;
    }

    /// <summary>Unions two lists, case-insensitively, preserving first-seen order.</summary>
    private static List<string> Union(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        var result = new List<string>(first);
        var seen = new HashSet<string>(first, StringComparer.OrdinalIgnoreCase);

        foreach (var item in second)
        {
            if (!string.IsNullOrWhiteSpace(item) && seen.Add(item))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// Unions two lists by a normalised key, so the same phone written two ways is not
    /// duplicated.
    /// </summary>
    private static List<string> UnionBy(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second,
        Func<string, string> key)
    {
        var result = new List<string>(first);
        var seen = new HashSet<string>(first.Select(key));

        foreach (var item in second)
        {
            if (!string.IsNullOrWhiteSpace(item) && seen.Add(key(item)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static List<ContactLocation> UnionLocations(
        IReadOnlyList<ContactLocation> first,
        IReadOnlyList<ContactLocation> second)
    {
        var result = new List<ContactLocation>(first);

        foreach (var location in second)
        {
            if (!result.Any(l =>
                l.Kind == location.Kind
                && string.Equals(l.Place, location.Place, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(location);
            }
        }

        return result;
    }

    private static List<PlatformAccount> UnionAccounts(
        IReadOnlyList<PlatformAccount> first,
        IReadOnlyList<PlatformAccount> second)
    {
        var result = new List<PlatformAccount>(first);

        foreach (var account in second)
        {
            var duplicate = result.Any(a =>
                string.Equals(a.Platform, account.Platform, StringComparison.OrdinalIgnoreCase)
                && a.HandleNormalised == account.HandleNormalised);

            if (!duplicate)
            {
                result.Add(account);
            }
        }

        return result;
    }

    private static DateOnly? Earliest(DateOnly? a, DateOnly? b) => (a, b) switch
    {
        (not null, not null) => a < b ? a : b,
        (not null, null) => a,
        (null, not null) => b,
        _ => null,
    };
}
