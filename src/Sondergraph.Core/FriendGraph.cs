namespace Sondergraph.Core;

/// <summary>A node in the friend graph.</summary>
/// <param name="Id">Person id, or <c>me</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="IsMe">Whether this is the centre.</param>
/// <param name="Tier">Relationship tier, for colouring.</param>
/// <param name="X">Layout position, -1..1.</param>
/// <param name="Y">Layout position, -1..1.</param>
/// <param name="Degree">How many friendships this person has within the graph.</param>
public sealed record GraphNode(
    string Id,
    string Name,
    bool IsMe,
    string Tier,
    double X,
    double Y,
    int Degree);

/// <summary>A friendship between two nodes.</summary>
/// <param name="From">One end.</param>
/// <param name="To">The other end.</param>
/// <param name="Cluster">1 when the edge involves the user, 2 between two contacts.</param>
public sealed record GraphEdge(string From, string To, int Cluster);

/// <summary>The friend network, positioned for drawing.</summary>
/// <param name="Nodes">Every node, the user first.</param>
/// <param name="Edges">Friendships.</param>
public sealed record FriendNetwork(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)
{
    /// <summary>How many contacts are in the network.</summary>
    public int ContactCount => Nodes.Count(n => !n.IsMe);

    /// <summary>How many friendships exist between contacts, excluding the user.</summary>
    public int FriendshipCount => Edges.Count(e => e.Cluster == 2);
}

/// <summary>
/// Builds the friend network: the user, their contacts, and who among them know each other.
/// </summary>
/// <remarks>
/// <para>
/// The layout is deterministic, not simulated. A force-directed layout would need a physics
/// loop, a new dependency, and would produce a different picture on every load - none of
/// which is worth it for a graph that is usually a hub with a few small clusters. Contacts
/// are placed on rings by relationship tier, so position carries meaning: close friends near
/// the centre, unrated contacts on the outside.
/// </para>
/// <para>
/// Only people with at least one recorded friendship appear beyond the first ring. Drawing
/// 1,700 isolated dots would bury the handful of real connections.
/// </para>
/// </remarks>
public static class FriendGraph
{
    /// <summary>Builds a network from the people in a workspace.</summary>
    public static FriendNetwork Build(IReadOnlyList<Person> people)
    {
        ArgumentNullException.ThrowIfNull(people);

        var byId = people.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        // Only contacts that are part of at least one friendship, plus their partners.
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in people)
        {
            foreach (var friend in person.FriendsWith)
            {
                if (byId.ContainsKey(friend))
                {
                    connected.Add(person.Id);
                    connected.Add(friend);
                }
            }
        }

        // Every contact the user knows gets an edge to the centre, but only the connected
        // ones are drawn as free-standing nodes; otherwise the picture is 1,700 spokes.
        var drawn = connected
            .Select(id => byId[id])
            .OrderBy(p => RelationshipOrder.Rank(p.Tier))
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var nodes = new List<GraphNode> { new("me", "You", true, "me", 0, 0, drawn.Count) };
        var edges = new List<GraphEdge>();
        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);

        // Ring by rank: family and best friends innermost, unrated outermost.
        var byRing = drawn.GroupBy(p => RelationshipOrder.Rank(p.Tier)).OrderBy(g => g.Key).ToList();
        var ring = 1;

        foreach (var group in byRing)
        {
            // Radius grows with the ring, but must stay inside the drawing area. The view
            // maps -1..1 across the canvas, so anything beyond 1 lands off-screen - the first
            // version reached 1.12 on the fifth ring and dropped a node below the bottom edge.
            // The cap keeps every ring visible while still separating them.
            var radius = Math.Min(0.30 + (ring * 0.13), 0.94);
            var count = group.Count();
            var index = 0;

            foreach (var person in group)
            {
                // Spread evenly around the circle, offset per ring so spokes do not align.
                var angle = (2 * Math.PI * index / Math.Max(count, 1)) + (ring * 0.4);
                var x = Math.Cos(angle) * radius;
                var y = Math.Sin(angle) * radius;

                positions[person.Id] = (x, y);
                nodes.Add(new GraphNode(
                    person.Id,
                    person.Name,
                    false,
                    ContactTierNames.ToWire(person.Tier),
                    Math.Round(x, 4),
                    Math.Round(y, 4),
                    person.FriendsWith.Count));

                edges.Add(new GraphEdge("me", person.Id, 1));
                index++;
            }

            ring++;
        }

        // Friend-to-friend edges, emitted once per unordered pair so the line is not doubled.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in drawn)
        {
            foreach (var friend in person.FriendsWith)
            {
                if (!positions.ContainsKey(friend) || !positions.ContainsKey(person.Id))
                {
                    continue;
                }

                var key = string.CompareOrdinal(person.Id, friend) < 0
                    ? person.Id + "|" + friend
                    : friend + "|" + person.Id;

                if (seen.Add(key))
                {
                    edges.Add(new GraphEdge(person.Id, friend, 2));
                }
            }
        }

        return new FriendNetwork(nodes, edges);
    }
}
