using Sondergraph.Core;

namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for grouping contacts by tag.
/// </summary>
/// <remarks>
/// The view exists to answer "what tags do I use, and how much" - so frequency, ordering and
/// the exclusion of collection-provenance tags are the behaviour worth pinning.
/// </remarks>
public sealed class TagGraphTests
{
    private static Person Someone(string id, string name, params string[] tags) => new()
    {
        Id = id,
        Name = name,
        Tags = tags,
    };

    [Fact]
    public void Groups_are_ordered_by_frequency()
    {
        var network = TagGraph.Build([
            Someone("a", "A", "work"),
            Someone("b", "B", "work", "family"),
            Someone("c", "C", "family"),
            Someone("d", "D", "family", "work", "travel"),
        ]);

        // "work" and "family" are both on three people, so the tie breaks alphabetically;
        // "travel" is on one. Asserting the counts rather than only the order is what makes
        // this test about frequency rather than about sorting.
        Assert.Equal(["family", "work", "travel"], network.Groups.Select(g => g.Tag));
        Assert.Equal([3, 3, 1], network.Groups.Select(g => g.Count));
    }

    [Fact]
    public void My_contacts_is_excluded()
    {
        // It is on 178 records because the vCard export puts every address-book entry in that
        // group. It says "this came from my phone", which distinguishes nothing, and as a
        // bubble it would dwarf every tag that carries meaning.
        var network = TagGraph.Build([
            Someone("a", "A", "myContacts", "work"),
            Someone("b", "B", "myContacts"),
        ]);

        Assert.DoesNotContain(network.Groups, g => g.Tag == "myContacts");
        Assert.Equal(["work"], network.Groups.Select(g => g.Tag));
    }

    [Fact]
    public void My_contacts_is_matched_case_insensitively()
    {
        var network = TagGraph.Build([Someone("a", "A", "MYCONTACTS", "work")]);

        Assert.DoesNotContain(network.Groups, g => string.Equals(g.Tag, "MYCONTACTS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_same_tag_twice_on_one_record_counts_once()
    {
        var network = TagGraph.Build([Someone("a", "A", "work", "work")]);

        Assert.Equal(1, network.Groups.Single(g => g.Tag == "work").Count);
    }

    [Fact]
    public void Tag_case_is_preserved_as_written()
    {
        // "sp" and "SP" are separate tags on disk. Folding them together here would rewrite
        // what the user typed without asking.
        var network = TagGraph.Build([Someone("a", "A", "sp"), Someone("b", "B", "SP")]);

        Assert.Equal(2, network.TagCount);
        Assert.Contains(network.Groups, g => g.Tag == "sp");
        Assert.Contains(network.Groups, g => g.Tag == "SP");
    }

    [Fact]
    public void Tagged_people_counts_each_person_once()
    {
        var network = TagGraph.Build([
            Someone("a", "A", "work", "family"),
            Someone("b", "B", "work"),
        ]);

        Assert.Equal(2, network.TaggedPeople);
    }

    [Fact]
    public void Businesses_and_places_are_not_tagged_for_the_view()
    {
        // The tag view is about people you know; a gym tagged "work" is not a contact group.
        var network = TagGraph.Build([
            new Person { Id = "gym", Name = "Gym", Kind = ContactKind.Business, Tags = ["work"] },
            Someone("a", "A", "work"),
        ]);

        Assert.Equal(1, network.Groups.Single(g => g.Tag == "work").Count);
    }

    [Fact]
    public void Members_are_ordered_by_relationship_then_name()
    {
        // Closest first, so the people worth seeing are at the front of a long tag.
        var network = TagGraph.Build([
            new Person { Id = "z", Name = "Zoe", Tier = ContactTier.Unknown, Tags = ["work"] },
            new Person { Id = "m", Name = "Mia", Tier = ContactTier.Best, Tags = ["work"] },
            new Person { Id = "a", Name = "Abe", Tier = ContactTier.Best, Tags = ["work"] },
        ]);

        Assert.Equal(["a", "m", "z"], network.Groups.Single().Members.Select(m => m.Id));
    }

    [Fact]
    public void A_workspace_with_no_tags_produces_nothing()
    {
        var network = TagGraph.Build([Someone("a", "A"), Someone("b", "B")]);

        Assert.Empty(network.Groups);
        Assert.Equal(0, network.TagCount);
        Assert.Equal(0, network.TaggedPeople);
        Assert.Null(network.Largest);
    }
}
