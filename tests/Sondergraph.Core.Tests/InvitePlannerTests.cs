namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for party invite suggestions.
/// </summary>
/// <remarks>
/// The planner is a suggestion engine, so the properties worth pinning are: it never
/// invents people, it says so when the cohort cannot meet the target, and the gender
/// balance is a target rather than something it silently substitutes.
/// </remarks>
public sealed class InvitePlannerTests
{
    private static Person P(string id, string gender, ContactTier tier = ContactTier.Unknown, string? place = null) =>
        new()
        {
            Id = id,
            Name = id,
            Gender = gender,
            Tier = tier,
            Locations = place is null ? [] : [new ContactLocation(place)],
        };

    private static List<Person> Crowd(int women, int men, ContactTier tier = ContactTier.Friend) =>
        [.. Enumerable.Range(0, women).Select(i => P($"f{i}", "F", tier)),
           .. Enumerable.Range(0, men).Select(i => P($"m{i}", "M", tier))];

    [Fact]
    public void A_balanced_cohort_meets_the_target_ratio()
    {
        // 100 is the smallest round size that expresses 51:49 exactly.
        var plan = InvitePlanner.Plan(
            Crowd(200, 200),
            new PartyPlanOptions(CohortScope.Everyone, [], FemaleShare: 0.51, Capacity: 100, Waves: 1));

        Assert.Equal(51, plan.Female);
        Assert.Equal(49, plan.Male);
    }

    [Fact]
    public void Short_waves_report_that_the_target_cannot_be_expressed()
    {
        // No integer split of 20 is 51:49; the nearest is 10:10. Saying nothing would leave
        // the user believing the plan hit a target it did not.
        var plan = InvitePlanner.Plan(
            Crowd(200, 200),
            new PartyPlanOptions(CohortScope.Everyone, [], Capacity: 20, Waves: 1));

        Assert.Contains(plan.Notes, n => n.Contains("cannot express"));
    }

    [Fact]
    public void Too_few_women_is_stated_rather_than_padded_with_men()
    {
        // The ratio is a target, not a guarantee; the failure has to be visible.
        var plan = InvitePlanner.Plan(
            Crowd(women: 2, men: 50),
            new PartyPlanOptions(CohortScope.Everyone, [], Capacity: 20, Waves: 1));

        Assert.Contains(plan.Notes, n => n.Contains("woman/women available"));
        Assert.True(plan.Female <= 2);
    }

    [Fact]
    public void Waves_are_filled_and_numbered_from_one()
    {
        var plan = InvitePlanner.Plan(
            Crowd(200, 200),
            new PartyPlanOptions(CohortScope.Everyone, [], Capacity: 10, Waves: 3));

        Assert.Equal([1, 2, 3], plan.ByWave.Select(g => g.Key));
        Assert.All(plan.ByWave, g => Assert.Equal(10, g.Count()));
    }

    [Fact]
    public void Nobody_is_suggested_twice()
    {
        var plan = InvitePlanner.Plan(
            Crowd(50, 50),
            new PartyPlanOptions(CohortScope.Everyone, [], Capacity: 10, Waves: 5));

        Assert.Equal(plan.Count, plan.Suggestions.Select(s => s.Person.Id).Distinct().Count());
    }

    [Fact]
    public void A_sparse_location_is_reported_not_silently_empty()
    {
        var people = new[] { P("a", "F", ContactTier.Friend, "Toronto") };

        var plan = InvitePlanner.Plan(
            people,
            new PartyPlanOptions(CohortScope.Location, ["Toronto"], Capacity: 10, Waves: 1));

        Assert.Single(plan.Suggestions);
        Assert.Contains(plan.Notes, n => n.Contains("does not hold enough"));
    }

    [Fact]
    public void An_unmatched_location_explains_itself()
    {
        var plan = InvitePlanner.Plan(
            Crowd(5, 5),
            new PartyPlanOptions(CohortScope.Location, ["Atlantis"], Capacity: 10, Waves: 1));

        Assert.Empty(plan.Suggestions);
        Assert.Contains(plan.Notes, n => n.Contains("no contacts match"));
    }

    [Fact]
    public void Closer_relationships_are_weighted_higher()
    {
        // Weighted sampling, not strict ordering: friends should dominate but an unrated
        // contact must remain possible, or the list is always the same handful of people.
        Assert.True(InvitePlanner.WeightFor(ContactTier.Friend) > InvitePlanner.WeightFor(ContactTier.Acquaintance));
        Assert.True(InvitePlanner.WeightFor(ContactTier.Acquaintance) > InvitePlanner.WeightFor(ContactTier.Unknown));
        Assert.True(InvitePlanner.WeightFor(ContactTier.Unknown) > 0);
    }

    [Fact]
    public void Friends_appear_more_often_than_unrated_over_many_seeds()
    {
        var people = new List<Person>();

        for (var i = 0; i < 5; i++)
        {
            people.Add(P($"friend{i}", "M", ContactTier.Friend));
            people.Add(P($"unrated{i}", "M", ContactTier.Unknown));
        }

        var friendHits = 0;

        for (var seed = 1; seed <= 40; seed++)
        {
            var plan = InvitePlanner.Plan(
                people,
                new PartyPlanOptions(CohortScope.Everyone, [], Capacity: 4, Waves: 1, Seed: seed));

            friendHits += plan.Suggestions.Count(
                s => s.Person.Id.StartsWith("friend", StringComparison.Ordinal));
        }

        // 40 seeds x 4 picks = 160 draws. Friends outweigh unrated four to one, so well over
        // half should be friends; the threshold is loose because this is sampling, not a
        // guarantee.
        Assert.True(friendHits > 80, $"friends chosen {friendHits} times of 160 draws");
    }

    [Fact]
    public void The_same_seed_produces_the_same_plan()
    {
        var people = Crowd(30, 30);
        var options = new PartyPlanOptions(CohortScope.Everyone, [], Capacity: 10, Waves: 2, Seed: 7);

        var first = InvitePlanner.Plan(people, options).Suggestions.Select(s => s.Person.Id).ToList();
        var second = InvitePlanner.Plan(people, options).Suggestions.Select(s => s.Person.Id).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void People_without_a_gender_are_still_suggested_and_counted_separately()
    {
        var people = new List<Person> { P("a", "F"), P("b", "M"), P("c", "") };

        var plan = InvitePlanner.Plan(
            people,
            new PartyPlanOptions(CohortScope.Everyone, [], Capacity: 10, Waves: 1));

        Assert.Equal(1, plan.UnrecordedGender);
        Assert.Equal(3, plan.Count);
    }
}
