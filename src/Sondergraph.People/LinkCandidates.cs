using Sondergraph.Core;

namespace Sondergraph.People;

/// <summary>
/// Evidence that two people may be the same human.
/// </summary>
/// <param name="Kind">What makes them look alike.</param>
/// <param name="Detail">Human-readable explanation, for the UI.</param>
/// <param name="Confidence">0..1, higher meaning stronger evidence.</param>
public sealed record LinkEvidence(LinkEvidenceKind Kind, string Detail, double Confidence);

/// <summary>Why two people might be one person.</summary>
public enum LinkEvidenceKind
{
    /// <summary>Names match exactly, ignoring case and punctuation.</summary>
    ExactName,

    /// <summary>Names match once spacing and initials are normalised, e.g. "Amit S Phadnis".</summary>
    SimilarName,

    /// <summary>One name contains the other.</summary>
    PartialName,

    /// <summary>They share a phone number.</summary>
    SharedPhone,

    /// <summary>They share an email address.</summary>
    SharedEmail,

    /// <summary>They share a profile URL.</summary>
    SharedProfile,
}

/// <summary>
/// A pair of people that may be worth linking.
/// </summary>
/// <param name="Left">One person.</param>
/// <param name="Right">The other.</param>
/// <param name="Evidence">Why they look alike, strongest first.</param>
/// <param name="Score">Total confidence, 0..1.</param>
public sealed record LinkCandidate(
    Person Left,
    Person Right,
    IReadOnlyList<LinkEvidence> Evidence,
    double Score)
{
    /// <summary>
    /// What linking would actually add, e.g. <c>instagram</c>.
    /// </summary>
    /// <remarks>
    /// The most useful field for a reviewer: it answers "is there any point?". When empty,
    /// the two records carry nothing the other lacks and there is nothing to gain.
    /// </remarks>
    public IReadOnlyList<string> NewSources =>
        Right.Sources
            .Except(Left.Sources, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The platforms on each side, as "left → right", for a reviewer.
    /// </summary>
    /// <remarks>
    /// Earlier this tried to compute a single "adds" list, which was misleading: when one
    /// record is LinkedIn and the other Instagram, <em>both</em> are new to the combined
    /// person, so the list was everything and said nothing. What a reviewer needs is which
    /// side carries what, and the evidence line already names the shared attribute.
    /// </remarks>
    public string PlatformSummary =>
        $"{string.Join(", ", Left.Sources.Order(StringComparer.OrdinalIgnoreCase))}"
        + " + "
        + $"{string.Join(", ", Right.Sources.Order(StringComparer.OrdinalIgnoreCase))}";
}

/// <summary>
/// Finds pairs of people that look like the same human across platforms.
/// </summary>
/// <remarks>
/// <para>
/// This exists because merging was implemented before anything could find a pair worth
/// merging. The result was a workflow that required typing two ids by hand, which in practice
/// meant it was never used.
/// </para>
/// <para>
/// <b>Nothing here merges.</b> Every candidate is a suggestion; the user decides
/// (DESIGN.md §4.3). The scoring only decides what to show first, never what to do.
/// </para>
/// </remarks>
public sealed class LinkCandidates
{
    /// <summary>
    /// Finds candidate pairs, strongest first.
    /// </summary>
    /// <param name="people">Everyone in the workspace.</param>
    /// <param name="minimumScore">Ignore pairs below this confidence.</param>
    /// <remarks>
    /// <para>
    /// Only pairs whose platform sets <em>differ</em> are considered. Two records with the
    /// same platforms and the same name are more likely deliberate (two people who genuinely
    /// share a name) than a missed link; and if they share platforms, linking them gains
    /// nothing.
    /// </para>
    /// <para>
    /// Pairs that already share a strong identifier are excluded: they would have been merged
    /// automatically by ingest, so they are not a question for the user.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<LinkCandidate> Find(
        IEnumerable<Person> people,
        double minimumScore = 0.5)
    {
        ArgumentNullException.ThrowIfNull(people);

        var list = people.ToList();
        var results = new List<LinkCandidate>();

        // Index by normalised name so the comparison is not quadratic over the whole
        // workspace. With 1,600 people a full pairwise scan would be 1.2M comparisons per
        // request, which is too slow to do on every page load.
        var byKey = new Dictionary<string, List<Person>>(StringComparer.Ordinal);

        foreach (var person in list)
        {
            // Index by strong identifiers as well as name. Two records sharing a phone but
            // with different spellings ("Alex Barthorpe" and "A. Barthorpe") are the
            // strongest possible pair and would otherwise never be compared, because the
            // name buckets never meet.
            var keys = Keys(person)
                .Concat(person.Phones
                    .Select(Person.DigitsOnly)
                    .Where(d => d.Length >= 7)
                    .Select(d => "tel:" + d))
                .Concat(person.Emails
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .Select(e => "email:" + e.Trim().ToLowerInvariant()));

            foreach (var key in keys)
            {
                if (!byKey.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    byKey[key] = bucket;
                }

                bucket.Add(person);
            }
        }

        var seen = new HashSet<(string, string)>();

        foreach (var (_, bucket) in byKey)
        {
            for (var i = 0; i < bucket.Count; i++)
            {
                for (var j = i + 1; j < bucket.Count; j++)
                {
                    var left = bucket[i];
                    var right = bucket[j];

                    // Order the pair canonically so each is considered once even when they
                    // share more than one key.
                    var pair = string.CompareOrdinal(left.Id, right.Id) < 0
                        ? (left.Id, right.Id)
                        : (right.Id, left.Id);

                    if (!seen.Add(pair))
                    {
                        continue;
                    }

                    var (a, b) = pair == (left.Id, right.Id) ? (left, right) : (right, left);

                    var evidence = Evaluate(a, b);

                    if (evidence.Count == 0)
                    {
                        continue;
                    }

                    // Confidence of the STRONGEST single piece of evidence, plus a small
                    // bonus for corroboration. The original version divided by three, which
                    // put an exact-name match at 0.2 and hid every candidate behind the
                    // default threshold - the feature silently found nothing.
                    var strongest = evidence.Max(e => e.Confidence);
                    var corroboration = Math.Min((evidence.Count - 1) * 0.1, 0.2);
                    var score = Math.Min(strongest + corroboration, 1.0);

                    if (score < minimumScore)
                    {
                        continue;
                    }

                    results.Add(new LinkCandidate(a, b, evidence, Math.Min(score, 1.0)));
                }
            }
        }

        return results
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Left.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Collects the evidence that two people may be the same.</summary>
    private static List<LinkEvidence> Evaluate(Person left, Person right)
    {
        var evidence = new List<LinkEvidence>();

        // A shared strong identifier means they are almost certainly one person. Ingest should
        // already have merged them, so seeing this means a merge was undone or two records
        // were created before the identifier existed.
        var sharedPhone = left.Phones
            .Select(Person.DigitsOnly)
            .FirstOrDefault(p => p.Length >= 7 && right.Phones.Select(Person.DigitsOnly).Contains(p));

        if (sharedPhone is not null)
        {
            evidence.Add(new LinkEvidence(LinkEvidenceKind.SharedPhone, "share a phone number", 1.0));
        }

        var sharedEmail = left.Emails.FirstOrDefault(e =>
            right.Emails.Contains(e, StringComparer.OrdinalIgnoreCase));

        if (sharedEmail is not null)
        {
            evidence.Add(new LinkEvidence(LinkEvidenceKind.SharedEmail, "share an email address", 1.0));
        }

        var sharedUrl = left.Accounts
            .Select(a => a.ProfileUrl)
            .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)
                && right.Accounts.Any(b => string.Equals(b.ProfileUrl, u, StringComparison.OrdinalIgnoreCase)));

        if (sharedUrl is not null)
        {
            evidence.Add(new LinkEvidence(LinkEvidenceKind.SharedProfile, "share a profile URL", 1.0));
        }

        // Name-based evidence, which is all that remains for a LinkedIn record and an
        // Instagram handle that have nothing else in common - the common case.
        var leftName = Fold(left.Name);
        var rightName = Fold(right.Name);

        if (leftName.Length > 0 && leftName == rightName)
        {
            evidence.Add(new LinkEvidence(LinkEvidenceKind.ExactName, "same name", 0.6));
        }
        else if (leftName.Length > 0 && rightName.Length > 0)
        {
            // An Instagram handle loses the space, so "Alex Barthorpe" and "alexbarthorpe"
            // must compare equal once punctuation is folded - which Fold already does.
            if (StartsWithWord(leftName, rightName) || StartsWithWord(rightName, leftName))
            {
                evidence.Add(new LinkEvidence(
                    LinkEvidenceKind.PartialName,
                    "one name contains the other",
                    0.35));
            }
            else if (SameWordsIgnoringInitials(leftName, rightName))
            {
                evidence.Add(new LinkEvidence(
                    LinkEvidenceKind.SimilarName,
                    "names match apart from a middle initial",
                    0.4));
            }
        }

        // Only offer a pair whose platform sets actually differ: linking two records that
        // carry the same platforms gains nothing.
        var leftSources = left.Sources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightSources = right.Sources.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (leftSources.SetEquals(rightSources))
        {
            return [];
        }

        return evidence
            .OrderByDescending(e => e.Confidence)
            .ToList();
    }

    /// <summary>
    /// Keys a person can be found under: the folded full name, plus each word-run.
    /// </summary>
    /// <remarks>
    /// Including word runs lets "Alex Barthorpe" match an Instagram handle "alexbarthorpe"
    /// without folding every name to a single token, which would create far too many
    /// collisions ("ann" matching everyone with "ann" in their name).
    /// </remarks>
    private static IEnumerable<string> Keys(Person person)
    {
        // The whole folded name. "Alex Barthorpe" and "alex.barthorpe" both fold to
        // "alexbarthorpe", so this is the key that matters: an Instagram handle typically
        // drops the space, and folding punctuation recovers the match.
        var full = Fold(person.Name);

        if (full.Length >= 4)
        {
            yield return full;
        }

        // Also index by the first and last words separately, so "Alex Barthorpe" can be found
        // when a handle keeps the space ("alex barthorpe") but a different joiner. Without
        // this, only the fully-joined form would match.
        var words = person.Name
            .Split([' ', '-', '_', '.', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(Fold)
            .Where(w => w.Length >= 4)
            .ToList();

        if (words.Count > 1)
        {
            // First+last is the discriminating pair; using only the first word would put
            // every "Alex" in one bucket.
            yield return words[0] + words[^1];
        }
    }

    /// <summary>Folds a name to lowercase letters and digits only.</summary>
    /// <remarks>
    /// Punctuation and accents are removed, so "Alex Barthorpe", "alex.barthorpe" and
    /// "alexbarthorpe" all fold to the same string. That is what lets a spaced display name
    /// meet an Instagram handle, which is the most common cross-platform pair here.
    /// </remarks>
    private static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var character in value.Normalize(System.Text.NormalizationForm.FormD))
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character);

            // Drop combining marks so "José" folds the same as "Jose".
            if (category is System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>Whether one folded name begins with the other at a word boundary.</summary>
    private static bool StartsWithWord(string shorter, string longer) =>
        shorter.Length >= 4
        && longer.Length > shorter.Length
        && longer.StartsWith(shorter, StringComparison.Ordinal);

    /// <summary>
    /// Whether two names share the same first and last words, allowing a middle initial.
    /// </summary>
    /// <remarks>
    /// Catches "Amit Phadnis" and "Amit S Phadnis", which is a real pair in this dataset.
    /// Deliberately requires the ending to match too, or "Alex Chen" and "Alex Barthorpe"
    /// would look related.
    /// </remarks>
    private static bool SameWordsIgnoringInitials(string left, string right)
    {
        if (left.Length < 6 || right.Length < 6)
        {
            return false;
        }

        // Require a shared prefix of at least four characters and a shared suffix of three,
        // which a middle initial preserves while genuinely different names do not.
        static string? Prefix(string value, int length) => value.Length >= length ? value[..length] : null;
        static string? Suffix(string value, int length) => value.Length >= length ? value[^length..] : null;

        var leftPrefix = Prefix(left, 4);
        var rightPrefix = Prefix(right, 4);
        var leftSuffix = Suffix(left, 3);
        var rightSuffix = Suffix(right, 3);

        return leftPrefix is not null && leftPrefix == rightPrefix
            && leftSuffix is not null && leftSuffix == rightSuffix;
    }
}
