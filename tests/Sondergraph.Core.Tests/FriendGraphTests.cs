using Sondergraph.Core;

namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for the network graph centre: who "you" is.
/// </summary>
/// <remarks>
/// The centre used to be a synthetic node with a hardcoded id and nothing behind it. It is now
/// the contact the user marked as themselves, so these tests pin both the fallback (nobody
/// marked) and the real-record behaviour.
/// </remarks>
public sealed class FriendGraphTests
{
    private static Person Someone(string id, string name, bool isMe = false, params string[] friends) => new()
    {
        Id = id,
        Name = name,
        IsMe = isMe,
        FriendsWith = friends,
    };

    [Fact]
    public void With_nobody_marked_the_centre_is_the_placeholder()
    {
        var network = FriendGraph.Build([
            Someone("ada", "Ada", friends: []),
            Someone("bo", "Bo", friends: ["ada"]),
        ]);

        var centre = network.Nodes.Single(n => n.IsMe);

        Assert.Equal("me", centre.Id);
        Assert.Equal("You", centre.Name);
    }

    [Fact]
    public void A_person_marked_as_self_becomes_the_centre()
    {
        // The point of the feature: the middle of the graph is a real record that can be
        // opened, rather than a label with no data behind it.
        var network = FriendGraph.Build([
            Someone("ada", "Ada", friends: []),
            Someone("bo", "Bo", friends: ["ada"]),
            Someone("sam", "Sam Example", isMe: true),
        ]);

        var centre = network.Nodes.Single(n => n.IsMe);

        Assert.Equal("sam", centre.Id);
        Assert.Equal("Sam Example", centre.Name);
        Assert.DoesNotContain(network.Nodes, n => n.Id == "me");
    }

    [Fact]
    public void The_self_record_is_not_drawn_as_a_spoke_of_itself()
    {
        // An edge from the centre to itself would draw a loop around the middle.
        var network = FriendGraph.Build([
            Someone("ada", "Ada"),
            Someone("bo", "Bo", friends: ["ada"]),
            Someone("sam", "Sam Example", isMe: true),
        ]);

        // No self-loop anywhere, and every spoke - the cluster-1 edges - starts at the centre.
        Assert.DoesNotContain(network.Edges, e => e.From == e.To);
        Assert.All(
            network.Edges.Where(e => e.Cluster == 1),
            e => Assert.Equal("sam", e.From));
    }

    [Fact]
    public void Contacts_are_still_connected_to_the_marked_centre()
    {
        var network = FriendGraph.Build([
            Someone("ada", "Ada"),
            Someone("bo", "Bo", friends: ["ada"]),
            Someone("sam", "Sam Example", isMe: true),
        ]);

        Assert.Contains(network.Edges, e => e.From == "sam" && e.To == "ada");
        Assert.Contains(network.Edges, e => e.From == "sam" && e.To == "bo");
    }

    [Fact]
    public void A_one_way_link_is_marked_as_not_reciprocal()
    {
        var network = FriendGraph.Build([
            Someone("ada", "Ada", friends: ["bo"]),
            Someone("bo", "Bo"),
        ]);

        var edge = network.Edges.Single(e => e.Cluster == 2);

        Assert.Equal("ada", edge.From);
        Assert.Equal("bo", edge.To);
        Assert.False(edge.Reciprocal);
        Assert.Equal(1, network.OneWayCount);
    }

    [Fact]
    public void A_link_listed_by_both_sides_is_reciprocal_and_drawn_once()
    {
        var network = FriendGraph.Build([
            Someone("ada", "Ada", friends: ["bo"]),
            Someone("bo", "Bo", friends: ["ada"]),
        ]);

        var edges = network.Edges.Where(e => e.Cluster == 2).ToList();

        // One line with two arrowheads, not two overlapping arrows.
        Assert.Single(edges);
        Assert.True(edges[0].Reciprocal);
        Assert.Equal(0, network.OneWayCount);
    }

    [Fact]
    public void The_user_edges_are_not_counted_as_one_way()
    {
        // Spokes from the centre are not a claim about anyone, so they are excluded from the
        // one-way tally that the summary line reports.
        var network = FriendGraph.Build([
            Someone("ada", "Ada", friends: ["bo"]),
            Someone("bo", "Bo"),
        ]);

        Assert.Equal(1, network.OneWayCount);
        Assert.All(network.Edges.Where(e => e.Cluster == 1), e => Assert.False(e.Reciprocal));
    }
}
