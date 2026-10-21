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

    /// <summary>Sets how this person prefers to be contacted, or clears it.</summary>
    /// <remarks>
    /// The channel is normalised on write, so "Signal" and "signal" cannot become two entries
    /// in the derived dropdown. An empty value clears the field rather than storing an empty
    /// string, which would otherwise count as a channel with no name.
    /// </remarks>
    public Result SetPreferredChannel(string personId, string? channel)
    {
        var normalised = PreferredChannels.Normalise(channel);

        return Mutate(personId, person => person with
        {
            PreferredChannel = normalised.Length == 0 ? null : normalised,
        });
    }

    /// <summary>Records how you know this person, or clears it.</summary>
    /// <remarks>
    /// Normalised on write so "Work" and "work" cannot become two entries in the derived
    /// suggestion list. An empty value clears the field rather than storing an empty string,
    /// which would otherwise count as a recorded connection with no content.
    /// </remarks>
    public Result SetHowWeMet(string personId, string? howWeMet)
    {
        var normalised = ConnectionKinds.Normalise(howWeMet);

        return Mutate(personId, person => person with
        {
            HowWeMet = normalised.Length == 0 ? null : normalised,
        });
    }

    /// <summary>
    /// Adds an account for a platform the export never covered.
    /// </summary>
    /// <param name="personId">Person to annotate.</param>
    /// <param name="platform">Platform key, e.g. <c>signal</c>.</param>
    /// <param name="handle">Handle, username or phone number on that platform.</param>
    /// <param name="profileUrl">Optional profile URL.</param>
    /// <remarks>
    /// <para>
    /// Ingest only knows the platforms it has exports for. Signal in particular has no
    /// contact export at all, so a Signal number or username can only ever be entered by
    /// hand. This is the general path for that: any platform, any handle.
    /// </para>
    /// <para>
    /// An existing account with the same platform and handle is replaced rather than
    /// duplicated, so correcting a typo does not leave two entries.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Deletes a person, if they contain nothing a user would miss.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists for records that should never have been contacts at all: GitHub thread
    /// notifications synced from a mailbox become people named after a repository, holding
    /// nothing but machine-generated addresses.
    /// </para>
    /// <para>
    /// The guard is the point. Deletion is irreversible, and the records most likely to look
    /// like junk are also the ones a merge may have absorbed real data into. A person with a
    /// phone, email, tier, tag, location, note or description is refused, so this can only
    /// remove genuine shells.
    /// </para>
    /// </remarks>
    public Result DeleteIfEmpty(string personId)
    {
        var loaded = _store.Load(personId);

        if (!loaded.IsOk)
        {
            return Result.Fail(loaded.Code, loaded.Message, loaded.Hint);
        }

        var person = loaded.ValueOrThrow();

        // Machine addresses do not count as content: they are precisely what makes a record
        // junk, so counting them made the guard refuse every record it exists to remove.
        // The first version did exactly that and removed nothing.
        var hasRealEmail = person.Emails.Any(ContactEmails.IsPersonAddress);

        var hasContent =
            person.Phones.Count > 0
            || hasRealEmail
            || person.Tier != ContactTier.Unknown
            || person.Tags.Count > 0
            || person.Locations.Count > 0
            || person.Notes.Count > 0
            || !string.IsNullOrWhiteSpace(person.HowWeMet)
            || !string.IsNullOrWhiteSpace(person.PreferredChannel);

        if (hasContent)
        {
            return Result.Fail(
                ErrorCode.Unknown,
                $"{person.Name} still has details recorded, so it was not deleted.");
        }

        // Moved aside rather than deleted, so a mistaken purge can be undone by moving the
        // directory back. Notes and media live inside it.
        var directory = _store.DirectoryFor(personId);

        if (!Directory.Exists(directory))
        {
            return Result.Fail(ErrorCode.Unknown, "That person's directory no longer exists.");
        }

        var trash = Path.Combine(_store.PeopleDirectory, ".trash");
        Directory.CreateDirectory(trash);

        var destination = Path.Combine(
            trash,
            $"{Path.GetFileName(directory)}-{DateTime.UtcNow:yyyyMMddHHmmss}");

        Directory.Move(directory, destination);

        return Result.Ok();
    }

    /// <summary>Records a person's gender, or clears it.</summary>
    /// <remarks>
    /// Trimmed but otherwise preserved exactly: the vocabulary belongs to the user, so "F"
    /// and "female" both stay as written. Clearing stores null rather than an empty string,
    /// because "recorded as blank" and "not recorded" should not be different states.
    /// </remarks>
    public Result SetGender(string personId, string? gender)
    {
        var value = gender?.Trim();

        return Mutate(personId, person => person with
        {
            Gender = string.IsNullOrWhiteSpace(value) ? null : value,

            // A human edit supersedes any earlier guess, so the inference marker goes with it.
            GenderSource = null,
        });
    }

    /// <summary>Records a gender inferred from the name, marking where it came from.</summary>
    /// <remarks>
    /// Separate from <see cref="SetGender"/> so the marker cannot be set by accident: a
    /// caller has to be explicit that this value is a guess.
    /// </remarks>
    public Result SetInferredGender(string personId, string gender)
    {
        return Mutate(personId, person => person with
        {
            Gender = string.IsNullOrWhiteSpace(gender) ? null : gender.Trim(),
            GenderSource = "inferred",
        });
    }

    /// <summary>Clears every gender that was inferred, leaving recorded ones alone.</summary>
    /// <returns>How many people were changed.</returns>
    public Result<int> ClearInferredGenders(IEnumerable<Person> people)
    {
        ArgumentNullException.ThrowIfNull(people);

        var cleared = 0;

        foreach (var person in people.Where(p => p.GenderWasInferred).ToList())
        {
            var result = Mutate(person.Id, p => p with { Gender = null, GenderSource = null });

            if (!result.IsOk)
            {
                return Result.Fail<int>(result.Code, result.Message, result.Hint);
            }

            cleared++;
        }

        return Result.Ok(cleared);
    }

    public Result AddAccount(string personId, string platform, string handle, string? profileUrl = null)
    {
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(handle))
        {
            return Result.Fail(ErrorCode.Unknown, "An account needs a platform and a handle.");
        }

        var account = PlatformAccount.Create(platform, handle, null, profileUrl);

        return Mutate(personId, person =>
        {
            var accounts = person.Accounts
                .Where(a => !string.Equals(a.Platform, account.Platform, StringComparison.OrdinalIgnoreCase)
                    || a.HandleNormalised != account.HandleNormalised)
                .Append(account with { FirstSeen = DateOnly.FromDateTime(DateTime.UtcNow) })
                .ToList();

            // Sources drive the source filter, so a hand-added account must register its
            // platform there too. Otherwise the person is filtered out of their own source.
            var sources = person.Sources.Contains(account.Platform, StringComparer.OrdinalIgnoreCase)
                ? person.Sources
                : [.. person.Sources, account.Platform];

            return person with { Accounts = accounts, Sources = sources };
        });
    }

    /// <summary>Removes an account by platform and handle.</summary>
    public Result RemoveAccount(string personId, string platform, string handle)
    {
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(handle))
        {
            return Result.Fail(ErrorCode.Unknown, "An account needs a platform and a handle.");
        }

        var normalised = PlatformAccount.NormaliseHandle(handle);

        return Mutate(personId, person =>
        {
            var accounts = person.Accounts
                .Where(a => !(string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase)
                    && a.HandleNormalised == normalised))
                .ToList();

            // Keep the source listed while any account on that platform remains.
            var remaining = accounts.Select(a => a.Platform).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sources = person.Sources
                .Where(s => !string.Equals(s, platform, StringComparison.OrdinalIgnoreCase)
                    || remaining.Contains(s))
                .ToList();

            return person with { Accounts = accounts, Sources = sources };
        });
    }

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
