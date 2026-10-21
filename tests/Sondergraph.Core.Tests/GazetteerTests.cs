namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for place coordinates and the distance weighting built on them.
/// </summary>
public sealed class GazetteerTests
{
    private static readonly GeoPlace Toronto = new("Toronto", 43.65348, -79.38393);
    private static readonly GeoPlace Waterloo = new("Waterloo", 43.4653, -80.5223);
    private static readonly GeoPlace Vancouver = new("Vancouver", 49.2609, -123.1140);

    [Fact]
    public void Distance_between_real_cities_matches_reality()
    {
        // Useful as a regression test: a mixed-up latitude or a flipped sign in the
        // haversine would show up as a wildly wrong number here.
        Assert.InRange(Toronto.DistanceKm(Waterloo), 90, 100);
        Assert.InRange(Toronto.DistanceKm(Vancouver), 3300, 3400);
    }

    [Fact]
    public void Distance_to_itself_is_zero()
    {
        Assert.Equal(0, Toronto.DistanceKm(Toronto), 6);
    }

    [Theory]
    [InlineData("Toronto")]
    [InlineData("toronto")]
    [InlineData("  Toronto  ")]
    [InlineData("TORONTO")]
    public void Place_lookup_ignores_case_and_surrounding_space(string query)
    {
        // Places are typed by hand, so the lookup must not be fussy.
        var gazetteer = new Gazetteer([Toronto]);

        Assert.NotNull(gazetteer.Find(query));
    }

    [Fact]
    public void An_unknown_place_has_no_coordinates()
    {
        Assert.Null(new Gazetteer([Toronto]).Find("Atlantis"));
    }

    [Fact]
    public void A_person_takes_the_closest_of_their_locations()
    {
        // Home in Toronto, work in Vancouver: a Toronto party should see them as local.
        var person = new Person
        {
            Id = "x",
            Name = "X",
            Locations = [new ContactLocation("Vancouver"), new ContactLocation("Toronto")],
        };

        var distance = new Gazetteer([Toronto, Vancouver]).Distance(person, Toronto);

        // Exactly zero, not merely small: the Toronto location must win outright, which is
        // what makes a multi-location person behave sensibly near either place.
        Assert.NotNull(distance);
        Assert.Equal(0, distance!.Value, 3);
    }

    [Fact]
    public void A_person_with_no_known_place_has_no_distance()
    {
        var person = new Person { Id = "x", Name = "X", Locations = [new ContactLocation("Atlantis")] };

        Assert.Null(new Gazetteer([Toronto]).Distance(person, Toronto));
    }

    [Fact]
    public void Distance_decays_weight_but_never_to_zero()
    {
        // A distant friend should rank below a nearby acquaintance yet must be able to appear
        // at all; a weight of zero would make them unselectable and invisible.
        var nearby = InvitePlanner.WeightFor(ContactTier.Friend, 0);
        var far = InvitePlanner.WeightFor(ContactTier.Friend, 5000);

        Assert.True(far < nearby);
        Assert.True(far > 0);
    }

    [Fact]
    public void A_nearby_acquaintance_outranks_a_distant_friend()
    {
        // The intended effect: a party you expect people to travel to should favour locals.
        var localAcquaintance = InvitePlanner.WeightFor(ContactTier.Acquaintance, 5);
        var distantFriend = InvitePlanner.WeightFor(ContactTier.Friend, 500);

        Assert.True(localAcquaintance > distantFriend);
    }

    [Fact]
    public void A_known_relationship_weight_is_unchanged_without_coordinates()
    {
        // Missing coordinate data must not silently penalise anyone.
        Assert.Equal(
            InvitePlanner.WeightFor(ContactTier.Friend),
            InvitePlanner.WeightFor(ContactTier.Friend, null));
    }

    [Fact]
    public void Suggesting_nearby_people_first_uses_distance()
    {
        var people = new List<Person>
        {
            new() { Id = "local", Name = "Local", Gender = "M", Locations = [new ContactLocation("Toronto")] },
            new() { Id = "far", Name = "Far", Gender = "M", Locations = [new ContactLocation("Vancouver")] },
        };

        var plan = InvitePlanner.Plan(
            people,
            new PartyPlanOptions(
                CohortScope.Everyone, [], Capacity: 1, Waves: 1,
                Venue: Toronto, Gazetteer: new Gazetteer([Toronto, Vancouver])));

        Assert.Single(plan.Suggestions);
        Assert.Equal("local", plan.Suggestions[0].Person.Id);
    }
}
