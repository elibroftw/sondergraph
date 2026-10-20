namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for the how-you-know-them vocabulary.
/// </summary>
/// <remarks>
/// Same shape as the channel tests, and for the same reason: the list is derived so a typo
/// cannot become a permanent category. The extra concern here is that entries are allowed to
/// be specific ("the climbing gym"), which makes normalisation and display matter more.
/// </remarks>
public sealed class ConnectionKindsTests
{
    private static Person With(string? how, string name = "Ada") => new()
    {
        Id = name.ToLowerInvariant(),
        Name = name,
        HowWeMet = how,
    };

    [Theory]
    [InlineData("  work  ", "work")]
    [InlineData("a  conference", "a conference")]
    [InlineData("Met at Maya's wedding", "Met at Maya's wedding")]
    [InlineData("Met at Maya's\nwedding", "Met at Maya's wedding")]
    public void Normalisation_preserves_the_wording(string input, string expected)
    {
        // This field is a human description, so normalisation tidies whitespace WITHOUT
        // slugifying. It once produced "met-at-maya-s-wedding", which mangles the sentence
        // the user typed - right for a category, wrong for prose.
        Assert.Equal(expected, ConnectionKinds.Normalise(input));
    }

    [Fact]
    public void Normalisation_keeps_the_users_own_casing()
    {
        // Capitalising would fight what the user wrote, and matching is case-insensitive
        // wherever it matters.
        Assert.Equal("Met at Maya's wedding", ConnectionKinds.Normalise("Met at Maya's wedding"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void An_absent_connection_normalises_to_empty(string? input, string expected)
    {
        Assert.Equal(expected, ConnectionKinds.Normalise(input));
    }

    [Theory]
    [InlineData("work", "work")]
    [InlineData("the climbing gym", "the climbing gym")]
    [InlineData("Met at Maya's wedding", "Met at Maya's wedding")]
    public void Display_returns_the_text_as_written(string input, string expected)
    {
        // Nothing to prettify now that values are prose rather than slugs.
        Assert.Equal(expected, ConnectionKinds.Display(input));
    }

    [Fact]
    public void The_list_contains_only_connections_in_use()
    {
        var inUse = ConnectionKinds.InUse([With("work"), With("university", "Bob")]);

        Assert.Equal(2, inUse.Count);
        Assert.Contains("work", inUse);
        Assert.Contains("university", inUse);
    }

    [Fact]
    public void Different_descriptions_stay_separate()
    {
        // The point of prose: "met at work" and "met at university" are not one entry.
        var inUse = ConnectionKinds.InUse([With("met at work"), With("met at university", "Bob")]);

        Assert.Equal(2, inUse.Count);
    }

    [Fact]
    public void A_connection_disappears_when_its_last_user_changes()
    {
        // The mechanism that prevents a typo becoming permanent.
        Assert.Contains("wrok", ConnectionKinds.InUse([With("wrok")]));
        Assert.DoesNotContain("wrok", ConnectionKinds.InUse([With("work")]));
    }

    [Fact]
    public void Descriptions_differing_only_in_whitespace_count_as_one()
    {
        Assert.Single(ConnectionKinds.InUse([With("the climbing gym"), With("the climbing  gym", "Bob")]));
    }

    [Fact]
    public void The_list_is_ordered_by_usage()
    {
        var people = new[]
        {
            With("work"), With("work", "B"), With("work", "C"),
            With("university", "D"),
        };

        Assert.Equal(["work", "university"], ConnectionKinds.InUse(people));
    }

    [Theory]
    [InlineData("wrok", "work")]
    [InlineData("univeristy", "university")]
    public void A_single_typo_is_flagged(string typed, string existing)
    {
        Assert.Equal(existing, ConnectionKinds.SuggestExisting(typed, [existing]));
    }

    [Fact]
    public void Work_and_workshop_are_not_treated_as_typos()
    {
        // Unlike channels, there is deliberately no prefix matching here: "work" and
        // "workshop" are plausibly separate connections.
        Assert.Null(ConnectionKinds.SuggestExisting("workshop", ["work"]));
    }

    [Fact]
    public void A_short_entry_is_never_flagged()
    {
        Assert.Null(ConnectionKinds.SuggestExisting("wrk", ["work"]));
    }

    [Fact]
    public void The_value_is_searchable()
    {
        // "who do I know from work?" is the reason to record it.
        Assert.Contains("work", With("work").AllIdentifiers());
    }
}
