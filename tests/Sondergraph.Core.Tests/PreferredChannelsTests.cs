namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for preferred-contact channels.
/// </summary>
/// <remarks>
/// The requirement behind these is "no leftovers like typos", which has two halves: the list
/// must not accumulate entries nobody uses, and a typo should be caught when it is made. The
/// first half is structural (the list is derived), the second is <see cref="PreferredChannels.SuggestExisting"/>.
/// </remarks>
public sealed class PreferredChannelsTests
{
    private static Person With(string? channel, string name = "Ada") => new()
    {
        Id = name.ToLowerInvariant(),
        Name = name,
        PreferredChannel = channel,
    };

    // ── normalisation ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Signal", "signal")]
    [InlineData("  signal  ", "signal")]
    [InlineData("SIGNAL", "signal")]
    [InlineData("Whats App", "whats app")]
    [InlineData("WhatsApp", "whatsapp")]
    public void Normalisation_prevents_spelling_variants_becoming_separate_channels(
        string input,
        string expected)
    {
        // Without this, "Signal" and "signal" would be two entries in the dropdown and the
        // user would have no way to tell them apart.
        Assert.Equal(expected, PreferredChannels.Normalise(input));
    }

    [Fact]
    public void Normalisation_collapses_a_doubled_space()
    {
        // A doubled space is a typo the user cannot see, so trimming alone is not enough.
        Assert.Equal("phone call", PreferredChannels.Normalise("phone  call"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void An_absent_channel_normalises_to_empty(string? input, string expected)
    {
        Assert.Equal(expected, PreferredChannels.Normalise(input));
    }

    // ── display ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("email", "Email")]
    [InlineData("whatsapp", "WhatsApp")]
    [InlineData("sms", "SMS")]
    [InlineData("signal", "Signal")]
    public void Known_channels_display_with_their_proper_capitalisation(string input, string expected)
    {
        // Stored lowercase, displayed properly - so the value in the file is uniform while the
        // UI is not shouting "WHATSAPP".
        Assert.Equal(expected, PreferredChannels.Display(input));
    }

    [Fact]
    public void An_unknown_channel_still_displays_tidily()
    {
        // A channel the user invented must not be shown as raw lowercase.
        Assert.Equal("Carrier Pigeon", PreferredChannels.Display("carrier pigeon"));
    }

    // ── the derived list ───────────────────────────────────────────────────────────

    [Fact]
    public void The_list_contains_only_channels_in_use()
    {
        // This is the mechanism that prevents leftovers: a channel that nobody has set simply
        // does not exist.
        var people = new[] { With("signal"), With("email", "Bob") };

        var inUse = PreferredChannels.InUse(people);

        Assert.Equal(2, inUse.Count);
        Assert.Contains("signal", inUse);
        Assert.Contains("email", inUse);
    }

    [Fact]
    public void A_channel_disappears_when_the_last_person_stops_using_it()
    {
        // Correcting the only user's channel must remove the typo from the list entirely,
        // rather than leaving it as an empty option.
        var before = new[] { With("sigmal") };
        Assert.Contains("sigmal", PreferredChannels.InUse(before));

        var after = new[] { With("signal") };
        Assert.DoesNotContain("sigmal", PreferredChannels.InUse(after));
    }

    [Fact]
    public void The_list_counts_each_channel_once_however_it_was_typed()
    {
        var people = new[] { With("Signal"), With("signal", "Bob"), With("SIGNAL", "Cy") };

        Assert.Equal(["signal"], PreferredChannels.InUse(people));
    }

    [Fact]
    public void The_list_is_ordered_by_usage_then_name()
    {
        // What people actually use should be at the top, not buried alphabetically.
        var people = new[]
        {
            With("email"), With("email", "B"), With("email", "C"),
            With("signal", "D"), With("signal", "E"),
            With("whatsapp", "F"),
        };

        Assert.Equal(["email", "signal", "whatsapp"], PreferredChannels.InUse(people));
    }

    [Fact]
    public void An_empty_workspace_has_no_channels_in_use()
    {
        Assert.Empty(PreferredChannels.InUse([]));
    }

    [Fact]
    public void People_without_a_channel_do_not_contribute()
    {
        Assert.Empty(PreferredChannels.InUse([With(null), With("", "B"), With("  ", "C")]));
    }

    // ── typo detection ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sigmal", "signal")]
    [InlineData("signl", "signal")]
    [InlineData("signals", "signal")]
    [InlineData("whatsap", "whatsapp")]
    [InlineData("ematil", "email")]
    public void A_single_character_typo_is_flagged(string typed, string existing)
    {
        // Deriving the list stops a typo becoming permanent, but it still lands on one person
        // where it is invisible. Catching it at entry is the only moment both spellings are
        // visible together.
        Assert.Equal(existing, PreferredChannels.SuggestExisting(typed, [existing]));
    }

    [Fact]
    public void A_prefix_is_flagged()
    {
        Assert.Equal("telegram", PreferredChannels.SuggestExisting("telegra", ["telegram"]));
    }

    [Fact]
    public void An_exact_match_is_not_flagged()
    {
        Assert.Null(PreferredChannels.SuggestExisting("signal", ["signal"]));
    }

    [Fact]
    public void A_genuinely_different_channel_is_not_flagged()
    {
        // A warning on legitimate input is worse than no warning: it teaches people to
        // ignore warnings.
        Assert.Null(PreferredChannels.SuggestExisting("matrix", ["signal", "email", "whatsapp"]));
    }

    [Theory]
    [InlineData("sme")]
    [InlineData("sm")]
    [InlineData("sns")]
    public void Very_short_names_are_never_flagged(string typed)
    {
        // "sms" and "smx" are plausibly different channels; there is too little signal in
        // three characters to claim one is a typo of the other.
        Assert.Null(PreferredChannels.SuggestExisting(typed, ["sms"]));
    }

    [Fact]
    public void An_empty_candidate_is_not_flagged()
    {
        Assert.Null(PreferredChannels.SuggestExisting(null, ["signal"]));
        Assert.Null(PreferredChannels.SuggestExisting("   ", ["signal"]));
    }
}
