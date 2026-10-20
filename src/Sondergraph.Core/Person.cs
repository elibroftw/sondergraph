using System.Globalization;
using System.Text;

namespace Sondergraph.Core;

/// <summary>
/// How close a contact is. Ordered from least to most.
/// </summary>
/// <remarks>
/// Deliberately small. These map onto the edge weights in DESIGN.md §4.1, and the ordering
/// is load-bearing: the mass contact view sorts by it, so the numeric values must not be
/// reordered without a migration.
/// </remarks>
public enum ContactTier
{
    /// <summary>Not yet rated.</summary>
    Unknown = 0,

    /// <summary>An acquaintance.</summary>
    Acquaintance = 1,

    /// <summary>A friend.</summary>
    Friend = 2,

    /// <summary>A close friend.</summary>
    Close = 3,

    /// <summary>A best friend.</summary>
    Best = 4,

    /// <summary>Family, which sits outside the friend scale.</summary>
    Family = 5,
}

/// <summary>
/// A platform account belonging to a person.
/// </summary>
/// <param name="Platform">Platform key, e.g. <c>linkedin</c>. Matches <c>data/raw/</c>.</param>
/// <param name="Handle">The platform's own identifier for this account.</param>
/// <param name="HandleNormalised">Lower-cased, trimmed form used for matching.</param>
/// <param name="DisplayName">Name as shown on that platform, when known.</param>
/// <param name="ProfileUrl">Canonical profile URL, when known.</param>
/// <param name="FirstSeen">When this account was first ingested.</param>
public sealed record PlatformAccount(
    string Platform,
    string Handle,
    string HandleNormalised,
    string? DisplayName = null,
    string? ProfileUrl = null,
    DateOnly? FirstSeen = null)
{
    /// <summary>Creates an account, normalising the handle.</summary>
    public static PlatformAccount Create(
        string platform,
        string handle,
        string? displayName = null,
        string? profileUrl = null,
        DateOnly? firstSeen = null) =>
        new(platform,
            handle,
            NormaliseHandle(handle),
            displayName,
            profileUrl,
            firstSeen);

    /// <summary>
    /// Normalises a handle for matching: trimmed, lower-cased, and stripped of a leading
    /// <c>@</c>.
    /// </summary>
    /// <remarks>
    /// Platforms disagree about whether handles carry a leading sigil, and users type them
    /// both ways. Without normalisation, <c>@ada</c> and <c>ada</c> become two accounts.
    /// </remarks>
    public static string NormaliseHandle(string handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var trimmed = handle.Trim().TrimStart('@').Trim();

        // Unicode-aware lower-casing: handles are not guaranteed ASCII.
        return trimmed.ToLowerInvariant();
    }
}

/// <summary>
/// Wire names for <see cref="ContactTier"/>.
/// </summary>
/// <remarks>
/// Lives in Core, next to the enum, so search and the file format share one mapping. A
/// duplicated copy is exactly the kind of thing that drifts silently: a tier renamed in one
/// place would make <c>tier:close</c> stop matching while the file still said "close".
/// </remarks>
public static class ContactTierNames
{
    /// <summary>Serialised name for a tier. Stable; changing one is a data migration.</summary>
    public static string ToWire(ContactTier tier) => tier switch
    {
        ContactTier.Acquaintance => "acquaintance",
        ContactTier.Friend => "friend",
        ContactTier.Close => "close",
        ContactTier.Best => "best",
        ContactTier.Family => "family",
        _ => "unknown",
    };

    /// <summary>Parses a wire name, defaulting to <see cref="ContactTier.Unknown"/>.</summary>
    public static ContactTier Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "acquaintance" => ContactTier.Acquaintance,
        "friend" => ContactTier.Friend,
        "close" => ContactTier.Close,
        "best" => ContactTier.Best,
        "family" => ContactTier.Family,
        _ => ContactTier.Unknown,
    };

    /// <summary>Human label, for display.</summary>
    public static string ToLabel(ContactTier tier) => tier switch
    {
        ContactTier.Acquaintance => "acquaintance",
        ContactTier.Friend => "friend",
        ContactTier.Close => "close friend",
        ContactTier.Best => "best friend",
        ContactTier.Family => "family",
        _ => "-",
    };
}

/// <summary>
/// One of a contact's locations.
/// </summary>
/// <param name="Place">Free-text place name, as the user typed it.</param>
/// <param name="Kind">What this location represents.</param>
/// <param name="Note">Optional clarification.</param>
public sealed record ContactLocation(
    string Place,
    LocationKind Kind = LocationKind.Other,
    string? Note = null);

/// <summary>What a location represents.</summary>
public enum LocationKind
{
    /// <summary>Where they live.</summary>
    Home = 0,

    /// <summary>Where they work.</summary>
    Work = 1,

    /// <summary>Somewhere they have been, or are from.</summary>
    Other = 2,
}

/// <summary>
/// A contact note, as markdown.
/// </summary>
/// <param name="FileName">File name within the person's <c>notes/</c> directory.</param>
/// <param name="Body">Markdown content.</param>
public sealed record ContactNote(string FileName, string Body);

/// <summary>
/// A person in the graph: one human, possibly with several platform accounts.
/// </summary>
/// <remarks>
/// <para>
/// This is the node type from DESIGN.md §4.1. It is a plain data carrier — reading and
/// writing happens in <c>Sondergraph.People</c>, so that this type can be used by parsers
/// and the UI without pulling in file I/O.
/// </para>
/// <para>
/// A person's identity is their <see cref="Id"/>, which is derived from their name and is
/// stable once written. Renaming the display name must not change the id.
/// </para>
/// </remarks>
public sealed record Person
{
    /// <summary>Stable identifier, also the directory name under <c>data/people/</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Display name, as the user wants to see it.</summary>
    public required string Name { get; init; }

    /// <summary>Alternative names this person is known by.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>How close this contact is.</summary>
    public ContactTier Tier { get; init; } = ContactTier.Unknown;

    /// <summary>Platform accounts belonging to this person.</summary>
    public IReadOnlyList<PlatformAccount> Accounts { get; init; } = [];

    /// <summary>Known locations.</summary>
    public IReadOnlyList<ContactLocation> Locations { get; init; } = [];

    /// <summary>Phone numbers, in original form.</summary>
    public IReadOnlyList<string> Phones { get; init; } = [];

    /// <summary>Email addresses.</summary>
    public IReadOnlyList<string> Emails { get; init; } = [];

    /// <summary>Free-form tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Notes attached to this person.</summary>
    public IReadOnlyList<ContactNote> Notes { get; init; } = [];

    /// <summary>Where the data about this person came from, e.g. <c>vcard</c>.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Fields that were machine-written and should not be hand-edited.</summary>
    public DateOnly? CreatedAt { get; init; }

    /// <summary>When this record was last modified.</summary>
    public DateOnly? UpdatedAt { get; init; }

    /// <summary>
    /// Every searchable identifier: name, aliases, account handles, and contact points.
    /// </summary>
    /// <remarks>
    /// Used by the mass contact view for free-text filtering, and by identity resolution
    /// as the fallback when no strong identifier matches.
    /// </remarks>
    public IEnumerable<string> AllIdentifiers()
    {
        yield return Name;

        foreach (var alias in Aliases)
        {
            yield return alias;
        }

        foreach (var account in Accounts)
        {
            yield return account.Handle;
            if (!string.IsNullOrWhiteSpace(account.DisplayName))
            {
                yield return account.DisplayName;
            }
        }

        foreach (var phone in Phones)
        {
            yield return phone;

            // The digits-only form as well, so that "(555) 010-0100" matches "5550100100".
            var digits = DigitsOnly(phone);
            if (digits.Length > 0)
            {
                yield return digits;
            }
        }

        foreach (var email in Emails)
        {
            yield return email;
        }

        // Locations are searchable too. Recording that someone lives in Berlin is only
        // useful if "who do I know in Berlin?" then works, and that question is the whole
        // reason to attach a location in the first place.
        foreach (var location in Locations)
        {
            yield return location.Place;

            if (!string.IsNullOrWhiteSpace(location.Note))
            {
                yield return location.Note;
            }
        }
    }

    /// <summary>Strips everything but digits from a phone number.</summary>
    public static string DigitsOnly(string phone)
    {
        ArgumentNullException.ThrowIfNull(phone);
        var builder = new StringBuilder(phone.Length);

        foreach (var character in phone)
        {
            if (char.IsAsciiDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Derives a url-safe, stable id from a name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Names are not unique, so the id is only a first guess; <c>PersonStore</c> resolves
    /// collisions by appending a discriminator. Deterministic for a given input so that
    /// re-running materialisation does not churn ids.
    /// </para>
    /// <para>
    /// Non-ASCII letters are transliterated where possible and dropped otherwise, because
    /// the id becomes a directory name and must be portable across filesystems.
    /// </para>
    /// </remarks>
    public static string DeriveId(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var normalised = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalised.Length);
        var lastWasDash = false;

        foreach (var character in normalised)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);

            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var id = builder.ToString().Trim('-');

        // A name with no usable ASCII characters still needs a stable id.
        if (id.Length == 0)
        {
            id = "person";

            foreach (var character in name)
            {
                id += ((int)character).ToString("x4", CultureInfo.InvariantCulture);
            }
        }

        // Keep directory names sane on filesystems with short path limits.
        return id.Length > 64 ? id[..64].Trim('-') : id;
    }
}
