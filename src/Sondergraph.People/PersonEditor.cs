using Sondergraph.Core;

namespace Sondergraph.People;

/// <summary>
/// Edits the user-owned fields of a person: relationship tier, tags and locations.
/// </summary>
/// <remarks>
/// <para>
/// These are the fields that make the contact graph <em>yours</em> rather than a dump of what
/// the platforms had. Everything else in a person record is written by ingest; these three
/// are authored, and ingest must never overwrite them (DESIGN.md §4.2).
/// </para>
/// <para>
/// Every operation here is a read-modify-write of one file, which is why they require the
/// workspace lock. They are deliberately small and explicit rather than a generic
/// "update any field" API: a caller that can set arbitrary fields can also clobber one
/// another, and the whole point of the split is that machine-owned and human-owned fields do
/// not drift into each other.
/// </para>
/// </remarks>
public sealed class PersonEditor
{
    private readonly PersonStore _store;

    /// <summary>Creates an editor over a store.</summary>
    public PersonEditor(PersonStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Sets how close a contact is.</summary>
    public Result SetTier(string personId, ContactTier tier) =>
        Mutate(personId, person => person with { Tier = tier });

    /// <summary>
    /// Adds a tag, keeping the list sorted and free of duplicates.
    /// </summary>
    /// <remarks>
    /// Sorted because tags are written to YAML in list order, so an unsorted list produces a
    /// different file every time the same tags are added in a different order — a persistent
    /// spurious diff.
    /// </remarks>
    public Result AddTag(string personId, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return Result.Fail(ErrorCode.Unknown, "A tag cannot be empty.");
        }

        var normalised = NormaliseTag(tag);

        return Mutate(personId, person =>
        {
            if (person.Tags.Contains(normalised, StringComparer.OrdinalIgnoreCase))
            {
                return person;
            }

            var tags = person.Tags
                .Append(normalised)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return person with { Tags = tags };
        });
    }

    /// <summary>Removes a tag. Removing one that is absent succeeds.</summary>
    public Result RemoveTag(string personId, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return Result.Fail(ErrorCode.Unknown, "A tag cannot be empty.");
        }

        var normalised = NormaliseTag(tag);

        return Mutate(personId, person => person with
        {
            Tags = person.Tags
                .Where(t => !string.Equals(t, normalised, StringComparison.OrdinalIgnoreCase))
                .ToList(),
        });
    }

    /// <summary>
    /// Sets the person's tags outright.
    /// </summary>
    /// <remarks>
    /// Useful for a bulk edit from the UI, where the user has rearranged a tag list and
    /// expects exactly that list to be stored.
    /// </remarks>
    public Result SetTags(string personId, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var cleaned = tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(NormaliseTag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Mutate(personId, person => person with { Tags = cleaned });
    }

    /// <summary>Adds a location, replacing one at the same place and kind.</summary>
    /// <remarks>
    /// Replacing rather than appending means correcting "London, work" updates the note on it
    /// instead of leaving two entries that disagree.
    /// </remarks>
    public Result AddLocation(string personId, string place, LocationKind kind = LocationKind.Other, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            return Result.Fail(ErrorCode.Unknown, "A location needs a place.");
        }

        var location = new ContactLocation(place.Trim(), kind, string.IsNullOrWhiteSpace(note) ? null : note.Trim());

        return Mutate(personId, person =>
        {
            var existing = person.Locations
                .Where(l => l.Kind != kind
                    || !string.Equals(l.Place, location.Place, StringComparison.OrdinalIgnoreCase))
                .ToList();

            existing.Add(location);

            return person with { Locations = existing };
        });
    }

    /// <summary>Removes every location matching a place.</summary>
    public Result RemoveLocation(string personId, string place)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            return Result.Fail(ErrorCode.Unknown, "A location needs a place.");
        }

        var wanted = place.Trim();

        return Mutate(personId, person => person with
        {
            Locations = person.Locations
                .Where(l => !string.Equals(l.Place, wanted, StringComparison.OrdinalIgnoreCase))
                .ToList(),
        });
    }

    /// <summary>Renames a person, keeping their id.</summary>
    /// <remarks>
    /// The id is deliberately not derived again: it is the directory name and the key that
    /// notes, media and merge records refer to. Changing it would orphan all of them, so a
    /// rename is a display-name change only.
    /// </remarks>
    public Result Rename(string personId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Fail(ErrorCode.Unknown, "A name cannot be empty.");
        }

        return Mutate(personId, person => person with { Name = name.Trim() });
    }

    /// <summary>
    /// Applies a change to a person and saves it.
    /// </summary>
    /// <remarks>
    /// A single read-modify-write, so the change is applied to what is on disk now rather than
    /// to a copy a caller has been holding — which would silently revert an edit made in
    /// between.
    /// </remarks>
    private Result Mutate(string personId, Func<Person, Person> change)
    {
        if (string.IsNullOrWhiteSpace(personId))
        {
            return Result.Fail(ErrorCode.Unknown, "A person id is required.");
        }

        var loaded = _store.Load(personId);

        if (!loaded.IsOk)
        {
            return Result.Fail(loaded.Code, loaded.Message, loaded.Hint);
        }

        var person = loaded.ValueOrThrow();
        var updated = change(person) with { UpdatedAt = DateOnly.FromDateTime(DateTime.UtcNow) };

        return _store.Save(updated);
    }

    /// <summary>
    /// Normalises a tag for storage: trimmed, lower-case, spaces as hyphens.
    /// </summary>
    /// <remarks>
    /// Tags are matched case-insensitively anyway, but storing one canonical form keeps files
    /// tidy and stops "Family", "family" and "FAMILY" appearing as three tags in an editor.
    /// Spaces become hyphens so a tag is usable as a search token.
    /// </remarks>
    internal static string NormaliseTag(string tag) =>
        tag.Trim().ToLowerInvariant().Replace(' ', '-');
}
