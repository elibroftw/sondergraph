namespace Sondergraph.Core;

/// <summary>
/// How you know the people in your contacts, as a derived vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// The same structure as <see cref="PreferredChannels"/>, for the same reason: the list is
/// <b>derived from the people who use it</b>, never stored. A connection context exists
/// exactly while somebody has it, so a typo cannot become a permanent category — it lives on
/// one person, where it is visible, and disappears when corrected.
/// </para>
/// <para>
/// This one is free-text rather than a fixed enum on purpose. "Work" and "university" are
/// common, but the interesting entries are specific: "the climbing gym", "Maya's wedding",
/// "that conference in Lisbon". A fixed list would force everyone into the generic half.
/// </para>
/// </remarks>
public static class ConnectionKinds
{
    /// <summary>
    /// Display form for common entries, so "work" reads as "Work".
    /// </summary>
    /// <remarks>
    /// Display only, never stored: the file keeps the normalised form, so the same connection
    /// cannot appear twice with different casing.
    /// </remarks>
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["work"] = "Work",
        ["university"] = "University",
        ["school"] = "School",
        ["family"] = "Family",
        ["childhood"] = "Childhood",
        ["neighbour"] = "Neighbour",
        ["hobby"] = "Hobby",
        ["online"] = "Online",
        ["conference"] = "Conference",
        ["volunteering"] = "Volunteering",
        ["church"] = "Church",
        ["gym"] = "Gym",
        ["sports"] = "Sports",
        ["dating"] = "Dating",
        ["met-travelling"] = "Met travelling",
    };

    /// <summary>
    /// Entries suggested in an empty workspace, before anyone has recorded one.
    /// </summary>
    /// <remarks>
    /// Used only when the derived list is empty, so a new user is not faced with a blank
    /// field that gives no clue what belongs there.
    /// </remarks>
    public static IReadOnlyList<string> DefaultSuggestions { get; } =
    [
        "work",
        "university",
        "school",
        "family",
        "childhood",
        "online",
    ];

    /// <summary>
    /// Normalises a connection for storage: trimmed, whitespace collapsed, lowercased,
    /// spaces as hyphens.
    /// </summary>
    /// <remarks>
    /// Hyphens rather than spaces so an entry can be typed into a smart query without quoting:
    /// <c>met:the-climbing-gym</c> and <c>met:"the climbing gym"</c> then mean the same thing.
    /// Lowercase so "Work" and "work" cannot become two categories in the suggestion list.
    /// </remarks>
    public static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var parts = value.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join('-', parts).ToLowerInvariant();
    }

    /// <summary>Human display form, e.g. <c>Work</c>, <c>The Climbing Gym</c>.</summary>
    public static string Display(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalised = Normalise(value);

        if (DisplayNames.TryGetValue(normalised, out var known))
        {
            return known;
        }

        // Title-case each hyphen-separated part; leave the rest untouched so a specific entry
        // like "Maya's wedding" keeps its own shape.
        return string.Join(
            ' ',
            normalised.Split('-').Select(word =>
                word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    /// <summary>
    /// Derives the connection list from the people who have one.
    /// </summary>
    /// <param name="people">Everyone in the workspace.</param>
    /// <returns>Normalised entries in use, most-used first, then alphabetically.</returns>
    /// <remarks>
    /// Ordered by usage then display name, so the connections that describe most of your
    /// contacts come first — which is what a suggestion list should offer.
    /// </remarks>
    public static IReadOnlyList<string> InUse(IEnumerable<Person> people)
    {
        ArgumentNullException.ThrowIfNull(people);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in people)
        {
            var normalised = Normalise(person.HowWeMet);

            if (normalised.Length == 0)
            {
                continue;
            }

            counts[normalised] = counts.GetValueOrDefault(normalised) + 1;
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => Display(kv.Key), StringComparer.CurrentCultureIgnoreCase)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Finds an existing entry that a new one looks like it meant to be.
    /// </summary>
    /// <remarks>
    /// Same conservative rule as channels: a near match is worth flagging, but only when
    /// there is enough text to be meaningful. Deliberately no prefix matching here, because
    /// "work" and "workshop" are plausibly separate entries, unlike "sigmal" and "signal".
    /// </remarks>
    public static string? SuggestExisting(string? candidate, IEnumerable<string> inUse)
    {
        var normalised = Normalise(candidate);

        if (normalised.Length < 4)
        {
            return null;
        }

        foreach (var existing in inUse)
        {
            var other = Normalise(existing);

            if (other.Length == 0 || string.Equals(other, normalised, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Two edits, not one: "wrok" to "work" is a transposition, which plain
            // Levenshtein scores as 2 even though it is the most common typo. Short entries
            // are excluded above, so this cannot fire on "gym" versus "gyn".
            if (Distance(normalised, other) <= 2)
            {
                return existing;
            }
        }

        return null;
    }

    /// <summary>Levenshtein distance, bounded by a length check.</summary>
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return int.MaxValue;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
