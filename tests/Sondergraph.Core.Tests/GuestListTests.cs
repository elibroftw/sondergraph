namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for guest lists and the gender grouping they report.
/// </summary>
/// <remarks>
/// The grouping is the part worth pinning down: it must account for every guest, put the
/// unrecorded ones in their own bucket rather than hiding them, and treat spelling variants
/// as one group without rewriting what the user typed.
/// </remarks>
public sealed class GuestListTests
{
    private static Person Guest(string id, string name, string? gender = null) => new()
    {
        Id = id,
        Name = name,
        Gender = gender,
    };

    [Theory]
    [InlineData("June 2027", "june-2027")]
    [InlineData("  Wedding  ", "wedding")]
    [InlineData("June 2027!", "june-2027")]
    [InlineData("Maya & Sam", "maya-sam")]
    [InlineData("Anniversary  2027", "anniversary-2027")]
    public void Slugs_are_stable_and_tidy(string name, string expected)
    {
        // Trailing separators are the usual failure: "June 2027!" would otherwise produce
        // "june-2027-", which is a poor file name and a different slug on every edit.
        Assert.Equal(expected, GuestList.SlugFor(name));
    }

    [Fact]
    public void An_all_punctuation_name_still_produces_a_slug()
    {
        // Not a usable file name otherwise, and failing here would be a bad way to learn.
        Assert.Equal("list", GuestList.SlugFor("!!!"));
    }

    [Fact]
    public void Guests_are_grouped_by_recorded_gender()
    {
        var list = new GuestList("x", "X", ["a", "b", "c"]);
        var resolved = new ResolvedGuestList(
            list,
            [Guest("a", "Ada", "F"), Guest("b", "Bob", "M"), Guest("c", "Cy", "F")],
            []);

        var groups = resolved.Groups;

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups.Single(g => g.Label == "F").Count);
        Assert.Equal(1, groups.Single(g => g.Label == "M").Count);
    }

    [Fact]
    public void Guests_without_a_gender_are_their_own_group_and_sorted_last()
    {
        // The unrecorded bucket must exist, or the counts silently fail to add up. It sorts
        // last because it is the residue rather than a category the user chose.
        var list = new GuestList("x", "X", ["a", "b"]);
        var resolved = new ResolvedGuestList(
            list,
            [Guest("a", "Ada"), Guest("b", "Bob", "M")],
            []);

        var groups = resolved.Groups;

        Assert.Equal(2, groups.Count);
        Assert.Equal("not recorded", groups[^1].Label);
        Assert.True(groups[^1].Unrecorded);
    }

    [Fact]
    public void Every_guest_appears_in_exactly_one_group()
    {
        // The property that makes the counts trustworthy.
        var list = new GuestList("x", "X", ["a", "b", "c", "d"]);
        var resolved = new ResolvedGuestList(
            list,
            [Guest("a", "Ada", "F"), Guest("b", "Bob", "M"), Guest("c", "Cy"), Guest("d", "Di", "f")],
            []);

        Assert.Equal(4, resolved.Groups.Sum(g => g.Count));
    }

    [Fact]
    public void Spelling_variants_group_together_but_keep_the_recorded_text()
    {
        // "F" and "f" are one group, and the label is not rewritten to a canonical form.
        var list = new GuestList("x", "X", ["a", "b"]);
        var resolved = new ResolvedGuestList(
            list,
            [Guest("a", "Ada", "F"), Guest("b", "Bea", "f")],
            []);

        var group = Assert.Single(resolved.Groups);

        Assert.Equal(2, group.Count);
        Assert.Equal("F", group.Label);
    }

    [Fact]
    public void Several_genders_are_all_preserved()
    {
        // Nothing is forced into male-or-female.
        var list = new GuestList("x", "X", ["a", "b", "c"]);
        var resolved = new ResolvedGuestList(
            list,
            [Guest("a", "Ada", "F"), Guest("b", "Bo", "M"), Guest("c", "Cy", "nonbinary")],
            []);

        Assert.Equal(3, resolved.Groups.Count);
        Assert.Contains(resolved.Groups, g => g.Label == "nonbinary");
    }

    [Fact]
    public void Unresolved_ids_are_reported_not_dropped()
    {
        // A guest list is a record of who was invited; a missing person must be visible.
        var list = new GuestList("x", "X", ["a", "gone"]);
        var resolved = new ResolvedGuestList(list, [Guest("a", "Ada")], ["gone"]);

        Assert.Single(resolved.Missing);
        Assert.Equal(1, resolved.Count);
    }

    [Fact]
    public void Members_are_sorted_by_name_within_a_group()
    {
        var list = new GuestList("x", "X", ["a", "b"]);
        var resolved = new ResolvedGuestList(
            list,
            [Guest("a", "Zoe", "F"), Guest("b", "Ada", "F")],
            []);

        Assert.Equal(["Ada", "Zoe"], resolved.Groups[0].People.Select(p => p.Name));
    }
}
