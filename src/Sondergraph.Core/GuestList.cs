namespace Sondergraph.Core;

/// <summary>
/// A named set of people invited to something.
/// </summary>
/// <param name="Slug">File-system identity, derived from the name.</param>
/// <param name="Name">Display name, as the user wrote it.</param>
/// <param name="Members">Person ids on the list, in the order they were added.</param>
/// <param name="Notes">Optional free text about the event.</param>
/// <param name="Date">When the party is, if known.</param>
/// <param name="Venue">Where it is, used to rank suggestions by distance.</param>
/// <remarks>
/// <para>
/// Members are person ids rather than embedded copies, so a guest list follows a person when
/// they are renamed or merged. Embedding a name would leave the list describing a record that
/// no longer exists.
/// </para>
/// <para>
/// An id that no longer resolves - a merge that was undone, or a person removed - is kept
/// rather than silently dropped. A guest list is a record of who was invited; losing an entry
/// quietly would misreport the event.
/// </para>
/// </remarks>
public sealed record GuestList(
    string Slug,
    string Name,
    IReadOnlyList<string> Members,
    string? Notes = null,
    DateOnly? Date = null,
    string? Venue = null)
{
    /// <summary>Whether the party has already happened, relative to today.</summary>
    /// <remarks>
    /// Used only for ordering the list of parties, never to hide anything: a past party is
    /// still the record of who came.
    /// </remarks>
    public bool IsPast => Date is { } date && date < DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>How many people are on the list.</summary>
    public int Count => Members.Count;

    /// <summary>
    /// A stable slug for a display name.
    /// </summary>
    /// <remarks>
    /// Lowercase, hyphenated, ASCII-ish. Trailing separators are trimmed, since "June 2027!"
    /// would otherwise produce "june-2027-".
    /// </remarks>
    public static string SlugFor(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var builder = new System.Text.StringBuilder(name.Length);
        var lastWasSeparator = false;

        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');

        // An all-punctuation name would produce an empty slug, which is not a usable file
        // name. Fall back to something stable rather than failing.
        return slug.Length == 0 ? "list" : slug;
    }
}

/// <summary>
/// A guest list with its members resolved against the current workspace.
/// </summary>
/// <param name="List">The stored list.</param>
/// <param name="Guests">Members whose ids resolve to a person.</param>
/// <param name="Missing">Member ids that no longer resolve.</param>
public sealed record ResolvedGuestList(
    GuestList List,
    IReadOnlyList<Person> Guests,
    IReadOnlyList<string> Missing)
{
    /// <summary>Guests grouped by recorded gender.</summary>
    /// <remarks>
    /// Grouped on the stored text, case-insensitively, so "F", "f" and "Female" are one group
    /// while still being shown as written. Anyone with no gender recorded falls into a single
    /// "not recorded" group rather than being omitted, so the count always adds up to the
    /// list size.
    /// </remarks>
    public IReadOnlyList<GuestGroup> Groups
    {
        get
        {
            var groups = new List<GuestGroup>();

            foreach (var group in Guests.GroupBy(
                g => string.IsNullOrWhiteSpace(g.Gender) ? null : g.Gender.Trim(),
                StringComparer.OrdinalIgnoreCase))
            {
                groups.Add(new GuestGroup(
                    group.Key ?? "not recorded",
                    group.Key is null,
                    group.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList()));
            }

            // Named groups first, alphabetically; "not recorded" last, because it is the
            // residue rather than a category the user chose.
            return groups
                .OrderBy(g => g.Unrecorded ? 1 : 0)
                .ThenBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Total guests who resolve.</summary>
    public int Count => Guests.Count;
}

/// <summary>One gender group within a resolved guest list.</summary>
/// <param name="Label">The gender as recorded, or "not recorded".</param>
/// <param name="Unrecorded">Whether this is the catch-all group.</param>
/// <param name="People">Guests in this group, by name.</param>
public sealed record GuestGroup(string Label, bool Unrecorded, IReadOnlyList<Person> People)
{
    /// <summary>How many people are in this group.</summary>
    public int Count => People.Count;
}
