namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for the reachability ordering within a relationship tier.
/// </summary>
/// <remarks>
/// The point is "who can I actually contact". Signal counts at the same level as a phone
/// number because it is keyed on one and is the channel many contacts prefer - but it can
/// appear on a person in three different ways, and missing any of them would make the order
/// depend on which one happened to be recorded.
/// </remarks>
public sealed class ReachabilityTests
{
    private static Person With(
        string[]? phones = null,
        string[]? emails = null,
        string[]? platforms = null,
        string? preferred = null) => new()
    {
        Id = "x",
        Name = "X",
        Phones = phones ?? [],
        Emails = emails ?? [],
        PreferredChannel = preferred,
        Accounts = (platforms ?? []).Select(p => PlatformAccount.Create(p, "handle")).ToList(),
    };

    [Fact]
    public void A_phone_number_is_most_reachable()
    {
        Assert.Equal(0, Reachability.Rank(With(phones: ["+1 555 0100"])));
    }

    [Fact]
    public void A_signal_account_is_as_reachable_as_a_phone()
    {
        // Signal is keyed on a phone number, so it is a direct personal route.
        Assert.Equal(0, Reachability.Rank(With(platforms: ["signal"])));
    }

    [Fact]
    public void Signal_as_the_preferred_channel_also_counts()
    {
        // Someone can prefer Signal without an account record ever being imported, since
        // Signal has no contact export at all.
        Assert.Equal(0, Reachability.Rank(With(preferred: "signal")));
    }

    [Fact]
    public void Signal_is_matched_case_insensitively()
    {
        Assert.Equal(0, Reachability.Rank(With(preferred: "Signal")));
    }

    [Fact]
    public void Email_alone_ranks_below_phone_and_signal()
    {
        Assert.Equal(1, Reachability.Rank(With(emails: ["ada@example.com"])));
    }

    [Fact]
    public void Email_with_a_phone_stays_at_the_top()
    {
        var rank = Reachability.Rank(With(phones: ["+1 555 0100"], emails: ["ada@example.com"]));

        Assert.Equal(0, rank);
    }

    [Fact]
    public void A_bare_social_handle_ranks_below_email()
    {
        Assert.Equal(2, Reachability.Rank(With(platforms: ["instagram"])));
    }

    [Fact]
    public void Nothing_at_all_ranks_last()
    {
        Assert.Equal(3, Reachability.Rank(With()));
    }

    [Fact]
    public void The_order_is_phone_and_signal_then_email_then_handle_then_nothing()
    {
        var ranked = new[]
        {
            With(),
            With(platforms: ["instagram"]),
            With(emails: ["ada@example.com"]),
            With(platforms: ["signal"]),
            With(phones: ["+1 555 0100"]),
        }
        .OrderBy(Reachability.Rank)
        .ToList();

        // The sorted sequence must be non-decreasing, and the two top-ranked entries must be
        // the phone and the Signal account rather than whichever came first alphabetically.
        Assert.Equal(
            ranked.Select(Reachability.Rank).OrderBy(r => r),
            ranked.Select(Reachability.Rank));

        Assert.Equal(0, Reachability.Rank(ranked[0]));
        Assert.Equal(0, Reachability.Rank(ranked[1]));
        Assert.Equal(1, Reachability.Rank(ranked[2]));
        Assert.Equal(2, Reachability.Rank(ranked[3]));
        Assert.Equal(3, Reachability.Rank(ranked[4]));
    }

    [Fact]
    public void HasSignal_reports_all_three_routes()
    {
        Assert.True(Reachability.HasSignal(With(platforms: ["signal"])));
        Assert.True(Reachability.HasSignal(With(preferred: "signal")));
        Assert.False(Reachability.HasSignal(With(platforms: ["instagram"])));
    }
}
