using System.Text;

namespace Sondergraph.Core;

/// <summary>
/// A parsed contact search: free text plus field filters.
/// </summary>
/// <remarks>
/// <para>
/// Supports a small, git-and-email-like query language so the list can be narrowed without a
/// form:
/// </para>
/// <code>
///   ada                       free text, matched against every identifier
///   platform:ig               contacts with an Instagram account
///   source:facebook           same thing under its other name
///   tier:close                relationship filter
///   has:phone                 contacts with a phone number
///   place:berlin              contacts with that location
///   platform:ig tier:friend ada   filters combine with AND
/// </code>
/// <para>
/// <b>Everything unknown is treated as free text.</b> That is the important design decision:
/// a user typing "platform" on its own, or "c++", or a name containing a colon, must not get
/// an empty list because the query parser rejected it. Unrecognised <c>key:value</c> pairs
/// fall back to a literal substring search, which is the least surprising behaviour.
/// </para>
/// <para>
/// Filters combine with AND because that is what people mean when they add one: each term
/// narrows the previous result.
/// </para>
/// </remarks>
public sealed record SearchQuery
{
    /// <summary>Free-text terms, all of which must match somewhere.</summary>
    public IReadOnlyList<string> Terms { get; init; } = [];

    /// <summary>Platforms at least one of which the contact must have.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = [];

    /// <summary>Relationship tiers, at least one of which must match.</summary>
    public IReadOnlyList<string> Tiers { get; init; } = [];

    /// <summary>Capabilities the contact must have, e.g. <c>phone</c>, <c>email</c>, <c>note</c>.</summary>
    public IReadOnlyList<string> Has { get; init; } = [];

    /// <summary>Places the contact must have one of.</summary>
    public IReadOnlyList<string> Places { get; init; } = [];

    /// <summary>Tags the contact must have one of.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>How-you-know-them values the contact must have one of.</summary>
    public IReadOnlyList<string> Met { get; init; } = [];

    /// <summary>
    /// Whether the contact must be reachable (has a phone or email), or must NOT be.
    /// </summary>
    /// <remarks>
    /// A tag can express "elusive", but it cannot express the thing that actually makes
    /// someone hard to get hold of: no way to contact them. <c>reachable:no</c> is derived
    /// from the data rather than being a note the user has to remember to keep in sync.
    /// </remarks>
    public bool? Reachable { get; init; }

    /// <summary>Whether the query constrains anything at all.</summary>
    public bool IsEmpty =>
        Terms.Count == 0 && Platforms.Count == 0 && Tiers.Count == 0
        && Has.Count == 0 && Places.Count == 0 && Tags.Count == 0
        && Met.Count == 0 && Reachable is null;

    /// <summary>
    /// Short names users actually type, mapped to platform keys.
    /// </summary>
    /// <remarks>
    /// The examples that motivated this: <c>ig</c> for Instagram and <c>fb</c> for Facebook.
    /// A user should not have to type "instagram" in full, and should not have to remember
    /// that the internal key is lowercase.
    /// </remarks>
    private static readonly Dictionary<string, string> PlatformAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ig"] = "instagram",
        ["insta"] = "instagram",
        ["gram"] = "instagram",
        ["fb"] = "facebook",
        ["meta"] = "facebook",
        ["li"] = "linkedin",
        ["in"] = "linkedin",
        ["vcf"] = "vcard",
        ["contacts"] = "vcard",
        ["vcard"] = "vcard",
        ["google"] = "vcard",
        ["takeout"] = "vcard",
        ["discord"] = "discord",
        ["dc"] = "discord",
        ["signal"] = "signal",
        ["bsky"] = "bluesky",
        ["bluesky"] = "bluesky",
        ["masto"] = "mastodon",
        ["mastodon"] = "mastodon",
    };

    /// <summary>Resolves a platform alias to its canonical key.</summary>
    public static string ResolvePlatform(string value) =>
        PlatformAliases.TryGetValue(value.Trim(), out var canonical)
            ? canonical
            : value.Trim().ToLowerInvariant();

    /// <summary>
    /// Parses a raw query string.
    /// </summary>
    /// <param name="raw">What the user typed.</param>
    public static SearchQuery Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new SearchQuery();
        }

        var terms = new List<string>();
        var platforms = new List<string>();
        var tiers = new List<string>();
        var has = new List<string>();
        var places = new List<string>();
        var tags = new List<string>();
        var met = new List<string>();
        bool? reachable = null;

        foreach (var token in Tokenise(raw))
        {
            var separator = token.IndexOf(':', StringComparison.Ordinal);

            // No colon, a leading colon, or a trailing colon: ordinary text.
            if (separator <= 0 || separator == token.Length - 1)
            {
                terms.Add(token);
                continue;
            }

            var field = token[..separator].Trim().ToLowerInvariant();
            var value = token[(separator + 1)..].Trim();

            if (value.Length == 0)
            {
                terms.Add(token);
                continue;
            }

            switch (field)
            {
                // "source" and "platform" are synonyms: the sources ARE platforms, and both
                // words are natural.
                case "platform" or "source" or "src" or "sm":
                    platforms.Add(ResolvePlatform(value));
                    break;

                case "tier" or "relationship" or "rel":
                    tiers.Add(value.ToLowerInvariant());
                    break;

                case "has":
                    has.Add(value.ToLowerInvariant());
                    break;

                case "place" or "location" or "city" or "where":
                    places.Add(value);
                    break;

                case "tag" or "label":
                    tags.Add(value.TrimStart('#'));
                    break;

                case "met" or "via" or "know":
                    met.Add(ConnectionKinds.Normalise(value));
                    break;

                case "reachable" or "contactable":
                    reachable = value.ToLowerInvariant() switch
                    {
                        "yes" or "y" or "true" or "1" => true,
                        "no" or "n" or "false" or "0" => false,
                        _ => null,
                    };

                    // An unparseable value constrains nothing; leaving it null rather than
                    // guessing keeps the query honest.
                    break;

                default:
                    // An unknown field is left as text rather than discarded. Dropping it
                    // would silently widen the result, which is worse than a slightly odd
                    // substring search.
                    terms.Add(token);
                    break;
            }
        }

        return new SearchQuery
        {
            Terms = terms,
            Platforms = platforms.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Tiers = tiers.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Has = has.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Places = places.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Met = met.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Reachable = reachable,
        };
    }

    /// <summary>
    /// Splits on whitespace, honouring double quotes so a multi-word value or phrase stays
    /// together.
    /// </summary>
    /// <remarks>
    /// Quoting matters for names: <c>"Ada Lovelace"</c> should be one term, not two that can
    /// match different people.
    /// </remarks>
    private static IEnumerable<string> Tokenise(string raw)
    {
        var current = new StringBuilder();
        var quoted = false;

        foreach (var character in raw)
        {
            if (character == '"')
            {
                quoted = !quoted;

                // A closing quote ends the token; an opening one just starts collecting.
                if (!quoted && current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>Whether a person satisfies this query.</summary>
    public bool Matches(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);

        if (Platforms.Count > 0)
        {
            var personPlatforms = person.Sources
                .Concat(person.Accounts.Select(a => a.Platform))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!Platforms.Any(personPlatforms.Contains))
            {
                return false;
            }
        }

        if (Tiers.Count > 0
            && !Tiers.Contains(ContactTierNames.ToWire(person.Tier), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var capability in Has)
        {
            var satisfied = capability switch
            {
                "phone" or "tel" => person.Phones.Count > 0,
                "email" => person.Emails.Count > 0,
                "note" or "notes" => person.Notes.Count > 0,
                "location" or "place" => person.Locations.Count > 0,
                "account" or "social" => person.Accounts.Count > 0,
                "tag" or "tags" => person.Tags.Count > 0,
                _ => true, // An unknown capability constrains nothing.
            };

            if (!satisfied)
            {
                return false;
            }
        }

        if (Tags.Count > 0
            && !Tags.Any(wanted => person.Tags.Any(have =>
                have.Contains(wanted, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        // Substring, not equality: "how we met" is prose, so met:wedding should find
        // "Met at Maya's wedding". Exact matching was right when the field held fixed
        // categories and would now miss almost every real description.
        if (Met.Count > 0)
        {
            var description = ConnectionKinds.Normalise(person.HowWeMet);

            if (!Met.Any(wanted =>
                description.Contains(wanted, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (Reachable is { } mustBeReachable)
        {
            var isReachable = person.Phones.Count > 0 || person.Emails.Count > 0;

            if (isReachable != mustBeReachable)
            {
                return false;
            }
        }

        if (Places.Count > 0)
        {
            var personPlaces = person.Locations.Select(l => l.Place).ToList();

            if (!Places.Any(wanted =>
                personPlaces.Any(have => have.Contains(wanted, StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }
        }

        if (Terms.Count == 0)
        {
            return true;
        }

        var identifiers = person.AllIdentifiers().ToList();

        return Terms.All(term =>
            identifiers.Any(i => i.Contains(term, StringComparison.OrdinalIgnoreCase))
            // A numeric term also matches on digits alone, so "555 0100" finds "+1 555-0100".
            || (Person.DigitsOnly(term) is { Length: >= 3 } digits
                && identifiers.Any(i => Person.DigitsOnly(i).Contains(digits, StringComparison.Ordinal))));
    }
}
