namespace Sondergraph.Core;

/// <summary>
/// Known contact channels and their display names.
/// </summary>
/// <remarks>
/// <para>
/// <b>The channel list is derived, not stored.</b> That is the whole design, and it follows
/// directly from the requirement that the dropdown never contains leftovers like typos:
/// </para>
/// <list type="bullet">
/// <item>
/// A stored list would keep an entry after the last person using it was changed, and would
/// accept "Sigmal" the first time someone typed it.
/// </item>
/// <item>
/// Deriving the list from the people who actually have a channel means a channel exists
/// precisely while somebody uses it. A typo appears only on the person it was typed for,
/// where it is visible and correctable, and disappears when corrected.
/// </item>
/// </list>
/// <para>
/// The known set below only supplies <em>display names</em> and the initial suggestions for a
/// fresh workspace. It never restricts what can be stored: a user grepping a novel channel
/// ("Matrix") can set it, and it then appears in the derived list like any other.
/// </para>
/// </remarks>
public static class PreferredChannels
{
    /// <summary>
    /// Display names for channels we know about, keyed by their normalised form.
    /// </summary>
    /// <remarks>
    /// Display only. A key present here is rendered with the given capitalisation; anything
    /// else is title-cased from its stored form, so an unknown channel still looks reasonable
    /// rather than being shown as raw lowercase.
    /// </remarks>
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["email"] = "Email",
        ["signal"] = "Signal",
        ["instagram"] = "Instagram",
        ["whatsapp"] = "WhatsApp",
        ["sms"] = "SMS",
        ["phone"] = "Phone call",
        ["telegram"] = "Telegram",
        ["messenger"] = "Messenger",
        ["facebook"] = "Facebook",
        ["linkedin"] = "LinkedIn",
        ["discord"] = "Discord",
        ["matrix"] = "Matrix",
        ["mastodon"] = "Mastodon",
        ["bluesky"] = "Bluesky",
        ["letter"] = "Letter",
    };

    /// <summary>
    /// Channels suggested in an empty workspace, before anyone has chosen one.
    /// </summary>
    /// <remarks>
    /// Only used to seed the dropdown when the derived list is empty, so a new user sees
    /// something sensible instead of a blank select. Once any channel is in use, the derived
    /// list takes over entirely and these suggestions stop appearing.
    /// </remarks>
    public static IReadOnlyList<string> DefaultSuggestions { get; } =
    [
        "email",
        "signal",
        "sms",
        "whatsapp",
        "instagram",
    ];

    /// <summary>
    /// Normalises a channel for storage: trimmed, internal whitespace collapsed, lowercased.
    /// </summary>
    /// <remarks>
    /// Lowercase so "Signal", "SIGNAL" and "signal" cannot become three entries in the
    /// dropdown. Whitespace is collapsed rather than merely trimmed so "phone  call" and
    /// "phone call" collapse too — a doubled space is a typo the user cannot see.
    /// </remarks>
    public static string Normalise(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return string.Empty;
        }

        var parts = channel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).ToLowerInvariant();
    }

    /// <summary>
    /// Human display form of a channel, e.g. <c>WhatsApp</c> for <c>whatsapp</c>.
    /// </summary>
    /// <remarks>
    /// Falls back to capitalising the stored form, so a channel the user invented still
    /// displays tidily. This is the only place capitalisation lives; it is never stored,
    /// because storing it would allow the same channel to appear with two spellings.
    /// </remarks>
    public static string Display(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return string.Empty;
        }

        var normalised = Normalise(channel);

        if (DisplayNames.TryGetValue(normalised, out var known))
        {
            return known;
        }

        // Title-case each word; leave the rest alone so "matrix-x" reads as "Matrix-x".
        return string.Join(
            ' ',
            normalised.Split(' ').Select(word =>
                word.Length == 0
                    ? word
                    : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    /// <summary>
    /// Derives the channel list from the people who use one.
    /// </summary>
    /// <param name="people">Everyone in the workspace.</param>
    /// <returns>
    /// Normalised channels in use, most-used first, then alphabetically.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Ordering is by usage then name, so the dropdown offers what people actually use at the
    /// top instead of a fixed alphabetical list that buries "Signal" under "Discord".
    /// </para>
    /// <para>
    /// The result is empty when nobody has set a channel; the caller decides whether to fall
    /// back to <see cref="DefaultSuggestions"/>.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> InUse(IEnumerable<Person> people)
    {
        ArgumentNullException.ThrowIfNull(people);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in people)
        {
            var normalised = Normalise(person.PreferredChannel);

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
    /// Whether a channel is close enough to a known one to look like a typo.
    /// </summary>
    /// <param name="candidate">The channel the user just typed.</param>
    /// <param name="inUse">Channels already in use.</param>
    /// <returns>The existing channel it resembles, or null.</returns>
    /// <remarks>
    /// <para>
    /// This is the other half of "no leftovers from typos". Deriving the list stops a typo
    /// creating a permanent entry, but the typo still lands on one person and is invisible
    /// there. Warning at the moment of entry is the only point where the user can see both
    /// spellings side by side.
    /// </para>
    /// <para>
    /// Deliberately conservative: an edit distance of one, or a prefix relationship, and
    /// never for very short names where "sms" and "smx" are plausibly different channels.
    /// A warning that fires on legitimate input is worse than no warning.
    /// </para>
    /// </remarks>
    public static string? SuggestExisting(string? candidate, IEnumerable<string> inUse)
    {
        var normalised = Normalise(candidate);

        if (normalised.Length < 4)
        {
            // Short names carry too little signal to compare safely.
            return null;
        }

        foreach (var existing in inUse)
        {
            var other = Normalise(existing);

            if (other.Length == 0 || string.Equals(other, normalised, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Two edits, not one: "sigmal" to "signal" is a transposition, which plain
            // Levenshtein scores as 2 even though it is the single most common typo. Only
            // applied to names long enough for it to be meaningful (checked above).
            if (Distance(normalised, other) <= 2
                || other.StartsWith(normalised, StringComparison.Ordinal)
                || normalised.StartsWith(other, StringComparison.Ordinal))
            {
                return existing;
            }
        }

        return null;
    }

    /// <summary>
    /// Levenshtein distance, capped at 2 for short-circuiting.
    /// </summary>
    /// <remarks>
    /// Textbook DP. Only ever called on short channel names, so the O(nm) cost is irrelevant
    /// and clarity is worth more than a faster algorithm.
    /// </remarks>
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
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
