namespace Sondergraph.Core;

/// <summary>A tag and everything filed under it.</summary>
/// <param name="Tag">The tag as written on the record.</param>
/// <param name="Count">How many people carry it.</param>
/// <param name="Members">The people carrying it, largest groups first by relationship.</param>
public sealed record TagGroup(string Tag, int Count, IReadOnlyList<TagMember> Members);

/// <summary>One person within a tag group.</summary>
/// <param name="Id">Person id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Tier">Relationship tier, for colouring.</param>
public sealed record TagMember(string Id, string Name, string Tier);

/// <summary>The tag landscape: every tag with its members, ordered by frequency.</summary>
public sealed record TagNetwork(IReadOnlyList<TagGroup> Groups, int TaggedPeople, int TagCount)
{
    /// <summary>The most widely applied tag, or null when nothing is tagged.</summary>
    public TagGroup? Largest => Groups.Count == 0 ? null : Groups[0];
}

/// <summary>
/// Groups contacts by tag, so the tagging can be looked at as a whole.
/// </summary>
/// <remarks>
/// <para>
/// A view of its own rather than an overlay on <see cref="FriendGraph"/>. The friendship graph
/// draws only people with a recorded mutual, which is 18 of the 191 tagged contacts in the
/// workspace that prompted this - layering tags onto it would have shown a tenth of the
/// tagging and given the impression the rest was missing.
/// </para>
/// <para>
/// <see cref="IgnoredTags"/> is subtracted here rather than in the view so every consumer
/// agrees on what the tags are.
/// </para>
/// </remarks>
public static class TagGraph
{
    /// <summary>
    /// Tags that describe how a record was collected rather than the person.
    /// </summary>
    /// <remarks>
    /// "myContacts" comes from the vCard export's group field and is on 178 records. It says
    /// "this came from my phone's address book", which is true of almost everything and
    /// therefore distinguishes nothing: as a group it is a third of the graph and drowns the
    /// tags that carry meaning. Kept on the records - it is accurate provenance - and left out
    /// of the tag view.
    /// </remarks>
    public static readonly IReadOnlySet<string> IgnoredTags =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myContacts" };

    /// <summary>Whether a tag is worth showing in the tag view.</summary>
    public static bool IsMeaningful(string tag) =>
        !string.IsNullOrWhiteSpace(tag) && !IgnoredTags.Contains(tag.Trim());

    /// <summary>Builds the tag landscape from the people in a workspace.</summary>
    public static TagNetwork Build(IReadOnlyList<Person> people)
    {
        ArgumentNullException.ThrowIfNull(people);

        // Tag text is case-sensitive on disk - "sp" and "SP" are separate tags - so grouping
        // is too. Folding them together here would silently rewrite what the user typed.
        var byTag = new Dictionary<string, List<Person>>(StringComparer.Ordinal);

        foreach (var person in people)
        {
            // A business or place is not somebody you know, and the tag view is about people.
            if (!person.IsPerson)
            {
                continue;
            }

            foreach (var tag in person.Tags)
            {
                var trimmed = tag.Trim();

                if (!IsMeaningful(trimmed))
                {
                    continue;
                }

                if (!byTag.TryGetValue(trimmed, out var list))
                {
                    list = [];
                    byTag[trimmed] = list;
                }

                // A record listing the same tag twice should count once.
                if (!list.Any(p => string.Equals(p.Id, person.Id, StringComparison.Ordinal)))
                {
                    list.Add(person);
                }
            }
        }

        var groups = byTag
            .Select(pair => new TagGroup(
                pair.Key,
                pair.Value.Count,
                pair.Value
                    .OrderBy(p => RelationshipOrder.Rank(p.Tier))
                    .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(p => new TagMember(p.Id, p.Name, ContactTierNames.ToWire(p.Tier)))
                    .ToList()))
            // Biggest tag first: the view's whole point is that frequency is the ordering.
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Tag, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var taggedPeople = byTag.Values
            .SelectMany(list => list)
            .Select(p => p.Id)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new TagNetwork(groups, taggedPeople, groups.Count);
    }
}
