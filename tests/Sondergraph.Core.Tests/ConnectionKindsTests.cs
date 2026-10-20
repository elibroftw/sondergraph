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
    [InlineData("Work", "work")]
    [InlineData("  work  ", "work")]
    [InlineData("the climbing gym", "the-climbing-gym")]
    [InlineData("The Climbing Gym", "the-climbing-gym")]
    [InlineData("the_climbing_gym", "the-climbing-gym")]
    [InlineData("a  conference", "a-conference")]
    public void Normalisation_makes_variants_one_entry(string input, string expected)
    {
        // Hyphens rather than spaces so the value can be typed into a smart query without
        // quoting, and lowercase so "Work" cannot become a second category.
        Assert.Equal(expected, ConnectionKinds.Normalise(input));
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
    [InlineData("work", "Work")]
    [InlineData("university", "University")]
    [InlineData("the-climbing-gym", "The Climbing Gym")]
    [InlineData("met-travelling", "Met travelling")]
    public void Display_is_readable_while_storage_is_normalised(string input, string expected)
    {
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
    public void A_connection_disappears_when_its_last_user_changes()
    {
        // The mechanism that prevents a typo becoming permanent.
        Assert.Contains("wrok", ConnectionKinds.InUse([With("wrok")]));
        Assert.DoesNotContain("wrok", ConnectionKinds.InUse([With("work")]));
    }

    [Fact]
    public void Variants_count_as_one_entry()
    {
        Assert.Equal(["work"], ConnectionKinds.InUse([With("Work"), With("work", "Bob")]));
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
