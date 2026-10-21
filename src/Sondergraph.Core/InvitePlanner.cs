namespace Sondergraph.Core;

/// <summary>Where a suggestion's candidates come from.</summary>
public enum CohortScope
{
    /// <summary>Everyone in the workspace.</summary>
    Everyone,

    /// <summary>People with a location matching one of the given places.</summary>
    Location,
}

/// <summary>How a party plan should be built.</summary>
/// <param name="Scope">Where to draw candidates from.</param>
/// <param name="Places">Places to match when the scope is <see cref="CohortScope.Location"/>.</param>
/// <param name="FemaleShare">Target share of women, 0..1. Defaults to 0.51.</param>
/// <param name="Capacity">People per wave.</param>
/// <param name="Waves">How many waves to plan.</param>
/// <param name="Seed">Seed for weighted sampling, so a plan can be reproduced.</param>
public sealed record PartyPlanOptions(
    CohortScope Scope,
    IReadOnlyList<string> Places,
    double FemaleShare = 0.51,
    int Capacity = 20,
    int Waves = 3,
    int Seed = 1);

/// <summary>One suggested invite, with why it was suggested.</summary>
/// <param name="Person">The contact.</param>
/// <param name="Wave">1-based wave number.</param>
/// <param name="Weight">Selection weight from their tier, higher meaning closer.</param>
public sealed record InviteSuggestion(Person Person, int Wave, double Weight)
{
    /// <summary>The tier that produced the weight.</summary>
    public string TierLabel => ContactTierNames.ToLabel(Person.Tier);
}

/// <summary>A plan, as a reviewable set of suggestions.</summary>
/// <param name="Suggestions">Suggested invites, wave by wave.</param>
/// <param name="Notes">Plain statements about what could not be satisfied.</param>
/// <param name="Female">Suggested women.</param>
/// <param name="Male">Suggested men.</param>
/// <param name="UnrecordedGender">Suggested people with no gender recorded.</param>
public sealed record PartyPlan(
    IReadOnlyList<InviteSuggestion> Suggestions,
    IReadOnlyList<string> Notes,
    int Female,
    int Male,
    int UnrecordedGender)
{
    /// <summary>Total suggestions.</summary>
    public int Count => Suggestions.Count;

    /// <summary>Suggestions grouped by wave, ascending.</summary>
    public IReadOnlyList<IGrouping<int, InviteSuggestion>> ByWave =>
        Suggestions.GroupBy(s => s.Wave).OrderBy(g => g.Key).ToList();
}

/// <summary>
/// Suggests who to invite, weighted by how well the user knows them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This suggests; it never creates a guest list.</b> The output is a ranked set of
/// candidates for the user to accept or ignore. Deciding who attends an event is not
/// something to automate, and a plan that silently became a list would be the wrong kind of
/// helpful.
/// </para>
/// <para>
/// <b>The gender ratio is a target, not a guarantee.</b> With a real graph it is often
/// unreachable: this workspace has 401 contacts with a recorded gender, and only a handful
/// carry both a location and a gender. When the cohort cannot meet the requested split the
/// plan says so in <see cref="PartyPlan.Notes"/> rather than quietly producing a different
/// thing.
/// </para>
/// </remarks>
public static class InvitePlanner
{
    /// <summary>Selection weight by relationship tier.</summary>
    /// <remarks>
    /// Relative, not absolute: a friend is worth about four unrated contacts. Unrated is
    /// deliberately nonzero, because 1,798 of 1,822 contacts here are unrated and excluding
    /// them would leave almost nothing to suggest.
    /// </remarks>
    public static double WeightFor(ContactTier tier) => tier switch
    {
        ContactTier.Family => 1.5,
        ContactTier.Best => 1.4,
        ContactTier.Close => 1.2,
        ContactTier.Friend => 1.0,
        ContactTier.Acquaintance => 0.6,
        _ => 0.25,
    };

    /// <summary>Builds a plan.</summary>
    public static PartyPlan Plan(IReadOnlyList<Person> people, PartyPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(options);

        var notes = new List<string>();
        var cohort = Select(options, people, notes);

        var female = cohort.Where(p => Is(p.Gender, "F")).ToList();
        var male = cohort.Where(p => Is(p.Gender, "M")).ToList();
        var other = cohort.Where(p => !Is(p.Gender, "F") && !Is(p.Gender, "M")).ToList();

        var total = options.Capacity * options.Waves;

        notes.Add(
            $"candidates: {cohort.Count} ({female.Count} F, {male.Count} M, {other.Count} with no gender)");

        var chosen = new List<InviteSuggestion>();

        // Draw women and men per wave against the target ratio, then top up from whoever is
        // left. The rod is the capacity, so a wave is always filled when the pool allows.
        for (var wave = 1; wave <= options.Waves; wave++)
        {
            // Per-wave integer split. At capacity 20 a 51:49 target rounds to 10:10, which
            // is 50%, not 51% - no integer split of 20 can express 51:49. The achieved split
            // is reported after the fact so the difference is visible rather than assumed.
            var wantedWomen = (int)Math.Round(options.Capacity * options.FemaleShare);
            var wantedMen = options.Capacity - wantedWomen;

            var availableWomen = female.Except(chosen.Select(c => c.Person)).ToList();
            var availableMen = male.Except(chosen.Select(c => c.Person)).ToList();

            var takeWomen = Math.Min(wantedWomen, availableWomen.Count);
            var takeMen = Math.Min(wantedMen, availableMen.Count);

            if (takeWomen < wantedWomen)
            {
                notes.Add(
                    $"wave {wave}: only {availableWomen.Count} woman/women available, "
                    + $"{wantedWomen} suggested by the {options.FemaleShare:P0} target");
            }

            if (takeMen < wantedMen)
            {
                notes.Add(
                    $"wave {wave}: only {availableMen.Count} man/men available, {wantedMen} suggested");
            }

            chosen.AddRange(Draw(availableWomen, takeWomen, wave, options.Seed + (wave * 31)));
            chosen.AddRange(Draw(availableMen, takeMen, wave, options.Seed + (wave * 37)));

            // Fill any shortfall from the remaining pool, unrecorded gender included, so a
            // wave is not left half-empty when eligible people exist.
            var filled = chosen.Count(c => c.Wave == wave);

            if (filled < options.Capacity)
            {
                var spare = cohort
                    .Except(chosen.Select(c => c.Person))
                    .ToList();

                // Materialised: Draw returns a lazy sequence, and .Count on it would be a
                // method group rather than the number of items.
                var topUp = Draw(spare, Math.Min(options.Capacity - filled, spare.Count), wave, options.Seed + (wave * 41));

                chosen.AddRange(topUp);

                if (topUp.Count > 0)
                {
                    notes.Add(
                        $"wave {wave}: topped up with {topUp.Count} more from the remaining pool");
                }
            }
        }

        if (chosen.Count < total)
        {
            notes.Add(
                $"only {chosen.Count} of {total} suggested - the cohort does not hold enough people");
        }

        // The achieved split is reported, because it will often differ from the target and
        // the difference is the interesting part.
        var finalFemale = chosen.Count(c => Is(c.Person.Gender, "F"));
        var finalMale = chosen.Count(c => Is(c.Person.Gender, "M"));

        var achieved = finalFemale + finalMale == 0
            ? 0
            : (double)finalFemale / (finalFemale + finalMale);

        notes.Add(
            $"achieved split: {finalFemale} F : {finalMale} M"
            + $" ({achieved:P0} female, target {options.FemaleShare:P0})");

        // A per-wave rounding of a lopsided target cannot hit it exactly on small waves.
        var perWaveWomen = (int)Math.Round(options.Capacity * options.FemaleShare);

        if (Math.Abs((double)perWaveWomen / options.Capacity - options.FemaleShare) > 0.005)
        {
            notes.Add(
                $"a wave of {options.Capacity} cannot express {options.FemaleShare:P0}:{1 - options.FemaleShare:P0} exactly; "
                + $"the nearest split is {perWaveWomen} F : {options.Capacity - perWaveWomen} M");
        }

        return new PartyPlan(
            chosen,
            notes,
            finalFemale,
            finalMale,
            chosen.Count - finalFemale - finalMale);
    }

    private static bool Is(string? value, string letter) =>
        string.Equals(value?.Trim(), letter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies the cohort scope.</summary>
    private static List<Person> Select(PartyPlanOptions options, IReadOnlyList<Person> people, List<string> notes)
    {
        if (options.Scope == CohortScope.Everyone)
        {
            return people.ToList();
        }

        var wanted = options.Places
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (wanted.Count == 0)
        {
            notes.Add("no places given, so nothing could be suggested");
            return [];
        }

        var matched = people
            .Where(p => p.Locations.Any(l =>
                wanted.Any(w => l.Place.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        // Stated plainly: a location-scoped plan over a sparse workspace is usually empty,
        // and finding that out from a blank list is worse than being told.
        if (matched.Count == 0)
        {
            notes.Add(
                $"no contacts match {string.Join(", ", wanted)}; "
                + "try --everyone to suggest from the whole graph");
        }

        return matched;
    }

    /// <summary>
    /// Draws <paramref name="count"/> people, preferring closer relationships without making
    /// the result determined by tier alone.
    /// </summary>
    /// <remarks>
    /// Weighted sampling rather than sort-by-tier-then-take: strict ordering would fill every
    /// wave with all the friends first and never consider anyone else, which makes for a
    /// strange party. The seed makes a plan reproducible, so a suggestion can be discussed and
    /// re-examined.
    /// </remarks>
    private static List<InviteSuggestion> Draw(
        List<Person> pool,
        int count,
        int wave,
        int seed)
    {
        if (count <= 0 || pool.Count == 0)
        {
            return [];
        }

        var random = new Random(seed);
        var remaining = pool.ToList();
        var drawn = new List<InviteSuggestion>(count);

        for (var i = 0; i < count && remaining.Count > 0; i++)
        {
            var weights = remaining.Select(p => WeightFor(p.Tier)).ToList();
            var total = weights.Sum();

            // Everyone left has zero weight; take them in order rather than looping forever.
            var pick = total <= 0
                ? 0
                : PickIndex(random, weights, total);

            var person = remaining[pick];
            remaining.RemoveAt(pick);
            drawn.Add(new InviteSuggestion(person, wave, WeightFor(person.Tier)));
        }

        return drawn;
    }

    private static int PickIndex(Random random, List<double> weights, double total)
    {
        var target = random.NextDouble() * total;
        var running = 0.0;

        for (var i = 0; i < weights.Count; i++)
        {
            running += weights[i];

            if (target <= running)
            {
                return i;
            }
        }

        return weights.Count - 1;
    }
}
