namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for the contact search syntax.
/// </summary>
/// <remarks>
/// The theme is "never surprise the user". Every case where a query could unexpectedly return
/// nothing gets a test, because an empty list looks like a bug in the app rather than a
/// misunderstanding of the syntax.
/// </remarks>
public sealed class SearchQueryTests
{
    private static Person Make(
        string name,
        string[]? sources = null,
        ContactTier tier = ContactTier.Unknown,
        string[]? phones = null,
        string[]? emails = null,
        string[]? places = null,
        string[]? tags = null,
        bool note = false) =>
        new()
        {
            Id = name.ToLowerInvariant().Replace(' ', '-'),
            Name = name,
            Sources = sources ?? [],
            Tier = tier,
            Phones = phones ?? [],
            Emails = emails ?? [],
            Tags = tags ?? [],
            Locations = (places ?? []).Select(p => new ContactLocation(p)).ToList(),
            Notes = note ? [new ContactNote("notes.md", "text")] : [],
        };

    // ── parsing ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Plain_text_becomes_a_term()
    {
        var query = SearchQuery.Parse("ada");

        Assert.Equal(["ada"], query.Terms);
        Assert.Empty(query.Platforms);
    }

    [Fact]
    public void Platform_filter_accepts_the_short_alias()
    {
        // The motivating example: platform:ig means Instagram.
        Assert.Equal(["instagram"], SearchQuery.Parse("platform:ig").Platforms);
    }

    [Theory]
    [InlineData("platform:ig", "instagram")]
    [InlineData("platform:instagram", "instagram")]
    [InlineData("source:fb", "facebook")]
    [InlineData("src:facebook", "facebook")]
    [InlineData("sm:li", "linkedin")]
    [InlineData("platform:vcf", "vcard")]
    [InlineData("platform:contacts", "vcard")]
    public void Platform_aliases_resolve_to_canonical_keys(string query, string expected)
    {
        Assert.Equal([expected], SearchQuery.Parse(query).Platforms);
    }

    [Fact]
    public void An_unknown_platform_is_kept_as_typed()
    {
        // Better to filter on a key that matches nothing than to silently ignore the filter.
        Assert.Equal(["myspace"], SearchQuery.Parse("platform:myspace").Platforms);
    }

    [Fact]
    public void Tier_filter_is_recognised()
    {
        Assert.Equal(["close"], SearchQuery.Parse("tier:close").Tiers);
    }

    [Fact]
    public void Capability_filters_are_recognised()
    {
        var query = SearchQuery.Parse("has:phone has:email");

        Assert.Equal(["phone", "email"], query.Has);
    }

    [Fact]
    public void Location_filter_is_recognised()
    {
        Assert.Equal(["berlin"], SearchQuery.Parse("place:berlin").Places);
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("platform:")]
    [InlineData(":ig")]
    [InlineData("c++")]
    [InlineData("ada:lovelace:extra")]
    public void Unrecognised_input_falls_back_to_text(string input)
    {
        // The critical rule: never reject a query and return nothing because it looked like a
        // filter. A user typing a name with a colon must still find that name.
        var query = SearchQuery.Parse(input);

        Assert.NotEmpty(query.Terms);
        Assert.True(query.IsEmpty || query.Terms.Count > 0);
    }

    [Fact]
    public void An_unknown_field_is_treated_as_text_not_discarded()
    {
        // Dropping it would silently widen the result, which is worse than an odd mismatch.
        var query = SearchQuery.Parse("foo:bar");

        Assert.Equal(["foo:bar"], query.Terms);
    }

    [Fact]
    public void Quoted_phrases_stay_together()
    {
        var query = SearchQuery.Parse("\"Ada Lovelace\" tier:friend");

        Assert.Equal(["Ada Lovelace"], query.Terms);
        Assert.Equal(["friend"], query.Tiers);
    }

    [Fact]
    public void Filters_and_terms_combine()
    {
        var query = SearchQuery.Parse("platform:ig tier:friend ada");

        Assert.Equal(["instagram"], query.Platforms);
        Assert.Equal(["friend"], query.Tiers);
        Assert.Equal(["ada"], query.Terms);
    }

    [Fact]
    public void Whitespace_only_is_empty()
    {
        Assert.True(SearchQuery.Parse("   ").IsEmpty);
        Assert.True(SearchQuery.Parse(null).IsEmpty);
    }

    [Fact]
    public void Field_names_are_case_insensitive()
    {
        Assert.Equal(["instagram"], SearchQuery.Parse("PLATFORM:IG").Platforms);
    }

    // ── matching ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Platform_filter_matches_by_source()
    {
        var person = Make("Ada", sources: ["instagram"]);
        var query = SearchQuery.Parse("platform:ig");

        Assert.True(query.Matches(person));
    }

    [Fact]
    public void Platform_filter_matches_by_account_even_when_sources_differ()
    {
        // A merged person may carry an account whose platform is not listed in sources.
        var person = Make("Ada", sources: ["vcard"]) with
        {
            Accounts = [PlatformAccount.Create("instagram", "ada")],
        };

        Assert.True(SearchQuery.Parse("platform:ig").Matches(person));
    }

    [Fact]
    public void Platform_filter_excludes_others()
    {
        var person = Make("Ada", sources: ["facebook"]);

        Assert.False(SearchQuery.Parse("platform:ig").Matches(person));
    }

    [Fact]
    public void Tier_filter_matches_the_relationship()
    {
        var person = Make("Ada", tier: ContactTier.Close);

        Assert.True(SearchQuery.Parse("tier:close").Matches(person));
        Assert.False(SearchQuery.Parse("tier:best").Matches(person));
    }

    [Fact]
    public void Has_phone_excludes_people_without_one()
    {
        var query = SearchQuery.Parse("has:phone");

        Assert.True(query.Matches(Make("Ada", phones: ["+1 555 0501"])));
        Assert.False(query.Matches(Make("Ada")));
    }

    [Fact]
    public void Has_note_and_location_work()
    {
        Assert.True(SearchQuery.Parse("has:note").Matches(Make("Ada", note: true)));
        Assert.True(SearchQuery.Parse("has:location").Matches(Make("Ada", places: ["Oslo"])));
        Assert.False(SearchQuery.Parse("has:location").Matches(Make("Ada")));
    }

    [Fact]
    public void Place_filter_matches_a_substring()
    {
        var person = Make("Ada", places: ["Greater London"]);

        Assert.True(SearchQuery.Parse("place:london").Matches(person));
    }

    [Fact]
    public void A_text_term_matches_the_name()
    {
        Assert.True(SearchQuery.Parse("ada").Matches(Make("Ada Lovelace")));
    }

    [Fact]
    public void A_text_term_is_required_not_merely_preferred()
    {
        // All terms must match: adding a term must narrow, never widen.
        var person = Make("Ada Lovelace");

        Assert.False(SearchQuery.Parse("ada zzz").Matches(person));
    }

    [Fact]
    public void A_phone_term_matches_on_digits_alone()
    {
        var person = Make("Ada", phones: ["+1 555-0500"]);

        Assert.True(SearchQuery.Parse("5550500").Matches(person));
    }

    [Fact]
    public void Filters_combine_with_and()
    {
        var person = Make("Ada", sources: ["instagram"], tier: ContactTier.Friend);

        Assert.True(SearchQuery.Parse("platform:ig tier:friend").Matches(person));
        Assert.False(SearchQuery.Parse("platform:ig tier:best").Matches(person));
    }

    [Fact]
    public void An_empty_query_matches_everyone()
    {
        Assert.True(SearchQuery.Parse("").Matches(Make("Anyone")));
    }

    [Fact]
    public void Multiple_platforms_are_any_of_not_all_of()
    {
        // "instagram or facebook" is what a person means by listing two.
        var query = SearchQuery.Parse("platform:ig platform:fb");

        Assert.True(query.Matches(Make("Ada", sources: ["instagram"])));
        Assert.True(query.Matches(Make("Ada", sources: ["facebook"])));
        Assert.False(query.Matches(Make("Ada", sources: ["linkedin"])));
    }
}
