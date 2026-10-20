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
