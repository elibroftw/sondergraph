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

    /// <summary>
    /// Immediate family: parents, siblings, children. Sits outside the friend scale.
    /// </summary>
    Family = 5,

    /// <summary>
    /// Extended family: cousins, aunts, uncles, in-laws.
    /// </summary>
    /// <remarks>
    /// Deliberately ranked between friends and acquaintances rather than next to immediate
    /// family. A cousin you see at weddings is not closer than your closest friends, and
    /// lumping the two together made "family" mean both the people you would call in an
    /// emergency and the ones you exchange greeting cards with.
    /// </remarks>
    ExtendedFamily = 6,
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
/// <summary>
/// How directly a person can be reached, for ordering contacts.
/// </summary>
/// <remarks>
/// <para>
/// Within one relationship tier the useful order is "who can I actually contact". A phone
/// number is the strongest signal because it works without an app; Signal counts alongside it
/// since it is keyed on a phone number and is the channel many contacts prefer.
/// </para>
/// <para>
/// Signal is recognised in every way it can appear on a person: as an account, as the
/// preferred channel, or as a phone number recorded against a Signal account. Checking only
/// one of them would give a different order depending on how the data arrived.
/// </para>
/// </remarks>
public static class Reachability
{
    /// <summary>Lower sorts earlier.</summary>
    public static int Rank(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);

        var hasPhone = person.Phones.Count > 0;
        var hasSignal = HasSignal(person);
        var hasEmail = person.Emails.Count > 0;

        return (hasPhone || hasSignal, hasEmail) switch
        {
            // Phone or Signal: can be reached directly and personally.
            (true, _) => 0,

            // Email only: reachable, but slower and less personal than a phone.
            (false, true) => 1,

            // Has some other account (an Instagram handle, a Discord username) but no direct
            // route to the person.
            _ when person.Accounts.Count > 0 => 2,

            // Nothing at all.
            _ => 3,
        };
    }

    /// <summary>Whether the person can be reached on Signal.</summary>
    public static bool HasSignal(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);

        return person.Accounts.Any(a => string.Equals(a.Platform, "signal", StringComparison.OrdinalIgnoreCase))
            || string.Equals(person.PreferredChannel, "signal", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The order relationships sort in, closest first.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from the <see cref="ContactTier"/> numeric values. Those run
/// Unknown=0 up to Family=5, which is the order the tiers were added, not the order anyone
/// would read them in. Sorting by the enum would put unrated contacts first and family last -
/// the exact opposite of useful.
/// </para>
/// <para>
/// Family sits above the friend scale rather than inside it: a family member is not a
/// "closer friend", and a list that interleaves the two reads strangely. Within the friend
/// scale the order is Best, Close, Friend, Acquaintance, then unrated last, because an
/// unrated contact is one you have not thought about yet.
/// </para>
/// </remarks>
public static class RelationshipOrder
{
    /// <summary>Sort rank for a tier; lower is closer.</summary>
    public static int Rank(ContactTier tier) => tier switch
    {
        ContactTier.Family => 0,
        ContactTier.Best => 1,
        ContactTier.Close => 2,
        ContactTier.Friend => 3,
        ContactTier.ExtendedFamily => 4,
        ContactTier.Acquaintance => 5,
        _ => 6,
    };
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
        ContactTier.ExtendedFamily => "extended-family",
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
        "extended-family" or "extended" or "extendedfamily" => ContactTier.ExtendedFamily,
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
        ContactTier.ExtendedFamily => "extended family",
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

    /// <summary>
    /// Ids of contacts this person is friends with, independent of how you know them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The edges of the social graph: A is friends with B, regardless of which platform
    /// either was found on. Without these the app models a star - you and everyone you know -
    /// which says nothing about who knows whom.
    /// </para>
    /// <para>
    /// <b>Friendship is symmetric, and that is enforced on write.</b> Adding A to B's list
    /// also adds B to A's, so the two records cannot disagree. Storing it on one side only
    /// would make the graph depend on which direction a traversal happened to walk.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> FriendsWith { get; init; } = [];

    /// <summary>
    /// The person's gender, or null when not recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Free-form rather than an enum, and never inferred. A fixed list would have to name
    /// which genders exist, which is a decision this app is not entitled to make on someone
    /// else's behalf; and guessing from a name would be both wrong often and an unpleasant
    /// thing to do with a backup of one's social graph.
    /// </para>
    /// <para>
    /// The reason it is a field at all is practical: guest lists are usually written
    /// male-and-female, often with a balance in mind. Anything the user types is preserved
    /// exactly as typed, so a household that writes "f" and one that writes "female" both
    /// work, and <see cref="GuestLists"/> groups on the stored text.
    /// </para>
    /// <para>
    /// Most contacts will be null. That is the expected state, not an error.
    /// </para>
    /// </remarks>
    public string? Gender { get; init; }

    /// <summary>
    /// How the gender was arrived at: <c>inferred</c> when guessed from the name, null when
    /// the user set it.
    /// </summary>
    /// <remarks>
    /// Exists because a guess is written into the same field a person's recorded gender
    /// would occupy, and the two must remain distinguishable. Without it, a wrong batch is
    /// indistinguishable from facts and unfixable without reading every contact; with it,
    /// <c>sg gender inferred --clear</c> reverts them in one step.
    /// </remarks>
    public string? GenderSource { get; init; }

    /// <summary>Whether the recorded gender was inferred rather than stated.</summary>
    public bool GenderWasInferred =>
        string.Equals(GenderSource, "inferred", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How the user knows this person, e.g. <c>work</c>, <c>university</c>, <c>the climbing
    /// gym</c>. Null when unset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a field and not a tag.</b> It is the only attribute that answers
    /// "should this person even be in my contacts?", and it is the one context that survives a
    /// merge: when two platform records become one, the connection is usually why they are the
    /// same person. A tag would be searchable but would lose that meaning — tags describe a
    /// person, this describes the relationship.
    /// </para>
    /// <para>
    /// Free-form and normalised on write, like <see cref="PreferredChannel"/>, for the same
    /// reason: the suggestion list is derived from usage, so a typo cannot become a permanent
    /// category.
    /// </para>
    /// </remarks>
    public string? HowWeMet { get; init; }

    /// <summary>
    /// How this person prefers to be contacted, or null when unset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A free-form string rather than an enum. The requirement is that the channel list
    /// contains only channels actually in use, with no leftovers from typos — and that is only
    /// guaranteed if the list is <em>derived</em> from the people who use them. A fixed enum
    /// could not grow ("Telegram", "Matrix") without a code change; a separate stored list
    /// would drift from what is actually written on people.
    /// </para>
    /// <para>
    /// Values are normalised on write (trimmed, lowercased) so "Signal", "signal" and
    /// " signal " are the same channel. Display capitalisation comes from
    /// <see cref="PreferredChannels"/> rather than being stored, so it cannot vary per record.
    /// </para>
    /// </remarks>
    public string? PreferredChannel { get; init; }

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

        // How you know someone is searchable: "who do I know from work?" is the point of
        // recording it.
        if (!string.IsNullOrWhiteSpace(HowWeMet))
        {
            yield return HowWeMet;
        }

        // The preferred channel is searchable: "who prefers Signal?" is the reason to record
        // it rather than just knowing it.
        if (!string.IsNullOrWhiteSpace(PreferredChannel))
        {
            yield return PreferredChannel;
        }

        // Tags are searchable. A tag you cannot search for is decoration: the point of
        // marking someone 'elusive' is to be able to list the elusive ones.
        foreach (var tag in Tags)
        {
            yield return tag;
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
