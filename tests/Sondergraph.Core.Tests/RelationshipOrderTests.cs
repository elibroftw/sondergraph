namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for the order relationships sort in.
/// </summary>
/// <remarks>
/// The requested order is family, best, close, friend, acquaintance, unrated. This is NOT the
/// enum's numeric order, which runs Unknown=0 to Family=5 - sorting by the enum would put
/// unrated first and family last. These tests exist to stop anyone "simplifying" the rank
/// back into a cast.
/// </remarks>
public sealed class RelationshipOrderTests
{
    [Fact]
    public void Ranks_follow_the_requested_order()
    {
        var ranked = new[]
        {
            ContactTier.Acquaintance,
            ContactTier.Unknown,
            ContactTier.Family,
            ContactTier.Friend,
            ContactTier.Best,
            ContactTier.Close,
        }
        .OrderBy(RelationshipOrder.Rank)
        .ToArray();

        Assert.Equal(
            [
                ContactTier.Family,
                ContactTier.Best,
                ContactTier.Close,
                ContactTier.Friend,
                ContactTier.Acquaintance,
                ContactTier.Unknown,
            ],
            ranked);
    }

    [Fact]
    public void Extended_family_sorts_between_friends_and_acquaintances()
    {
        // The requested order. A cousin you see at weddings is not closer than your closest
        // friends, but is more than an acquaintance.
        Assert.True(RelationshipOrder.Rank(ContactTier.Friend) < RelationshipOrder.Rank(ContactTier.ExtendedFamily));
        Assert.True(RelationshipOrder.Rank(ContactTier.ExtendedFamily) < RelationshipOrder.Rank(ContactTier.Acquaintance));
    }

    [Fact]
    public void Immediate_family_still_ranks_above_extended_family()
    {
        Assert.True(RelationshipOrder.Rank(ContactTier.Family) < RelationshipOrder.Rank(ContactTier.ExtendedFamily));
    }

    [Fact]
    public void The_full_order_is_family_best_close_friend_extended_acquaintance_business_unrated()
    {
        var ranked = new[]
        {
            ContactTier.Unknown,
            ContactTier.Business,
            ContactTier.Acquaintance,
            ContactTier.ExtendedFamily,
            ContactTier.Friend,
            ContactTier.Close,
            ContactTier.Best,
            ContactTier.Family,
        }
        .OrderBy(RelationshipOrder.Rank)
        .ToArray();

        Assert.Equal(
            [
                ContactTier.Family,
                ContactTier.Best,
                ContactTier.Close,
                ContactTier.Friend,
                ContactTier.ExtendedFamily,
                ContactTier.Acquaintance,
                ContactTier.Business,
                ContactTier.Unknown,
            ],
            ranked);
    }

    [Fact]
    public void Business_sorts_below_acquaintances_and_above_unrated()
    {
        // Knowing someone through work is weaker than knowing them socially, but it is still
        // a relationship and should rank above a record nobody has rated.
        Assert.True(RelationshipOrder.Rank(ContactTier.Acquaintance) < RelationshipOrder.Rank(ContactTier.Business));
        Assert.True(RelationshipOrder.Rank(ContactTier.Business) < RelationshipOrder.Rank(ContactTier.Unknown));
    }

    [Fact]
    public void Business_tier_is_distinct_from_business_kind()
    {
        // The tier is how you know the person; the kind is what the record is. They share a
        // name, so the values must never be confused for one another.
        Assert.NotEqual((int)ContactTier.Business, (int)ContactKind.Business);
    }

    [Fact]
    public void Business_tier_round_trips_through_its_wire_form()
    {
        Assert.Equal("business", ContactTierNames.ToWire(ContactTier.Business));
        Assert.Equal(ContactTier.Business, ContactTierNames.Parse("business"));
        Assert.Equal(ContactTier.Business, ContactTierNames.Parse("Business"));
        Assert.Equal(ContactTier.Business, ContactTierNames.Parse("colleague"));
        Assert.Equal(ContactTier.Business, ContactTierNames.Parse("work"));
    }

    [Fact]
    public void The_wire_form_is_a_slug_and_round_trips()
    {
        // The wire form is written into person.md, so it must be stable and parseable back.
        var wire = ContactTierNames.ToWire(ContactTier.ExtendedFamily);

        Assert.Equal("extended-family", wire);
        Assert.Equal(ContactTier.ExtendedFamily, ContactTierNames.Parse(wire));
    }

    [Fact]
    public void Family_sorts_before_every_friend_tier()
    {
        // Family sits above the friend scale rather than inside it.
        Assert.True(RelationshipOrder.Rank(ContactTier.Family) < RelationshipOrder.Rank(ContactTier.Best));
    }

    [Fact]
    public void Unrated_sorts_last()
    {
        // An unrated contact is one you have not thought about yet; it should not lead.
        var all = Enum.GetValues<ContactTier>();

        Assert.All(all, tier =>
        {
            if (tier != ContactTier.Unknown)
            {
                Assert.True(RelationshipOrder.Rank(tier) < RelationshipOrder.Rank(ContactTier.Unknown));
            }
        });
    }

    [Fact]
    public void The_rank_is_not_the_enum_value()
    {
        // Guards the trap: casting to int is the obvious "simplification" and produces the
        // exact inverse of the intended order.
        Assert.NotEqual((int)ContactTier.Family, RelationshipOrder.Rank(ContactTier.Family));
        Assert.NotEqual((int)ContactTier.Best, RelationshipOrder.Rank(ContactTier.Best));
    }
}
