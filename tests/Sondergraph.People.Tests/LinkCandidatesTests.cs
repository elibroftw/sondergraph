using Sondergraph.Core;

namespace Sondergraph.People.Tests;

/// <summary>
/// Tests for finding cross-platform pairs worth linking.
/// </summary>
/// <remarks>
/// The point of this class is that merging worked but nothing could find a pair to merge, so
/// the feature was unusable in practice. These tests pin the two failure modes that made it
/// silently useless: a name key that never collides, and a score that falls below the
/// threshold.
/// </remarks>
public sealed class LinkCandidatesTests
{
    private static Person P(string id, string name, params string[] sources) => new()
    {
        Id = id,
        Name = name,
        Sources = sources,
    };

    [Fact]
    public void Finds_a_spaced_name_and_an_underscored_handle()
    {
        // The real case: a LinkedIn record and an Instagram handle with the same name.
        var found = LinkCandidates.Find(
        [
            P("abdullah-dabbagh", "Abdullah Dabbagh", "linkedin"),
            P("abdullah-dabbagh-2", "abdullah_dabbagh_", "instagram"),
        ]);

        Assert.Single(found);
        Assert.Contains(found[0].Evidence, e => e.Kind == LinkEvidenceKind.ExactName);
    }

    [Fact]
    public void Finds_a_name_and_a_dotted_handle()
    {
        // The bug that made the first implementation find nothing: splitting on punctuation
        // produced words that could not collide with the joined form.
        var found = LinkCandidates.Find(
        [
            P("alex-barthorpe", "Alex Barthorpe", "linkedin"),
            P("alex-barthorpe-2", "alex.barthorpe", "instagram"),
        ]);

        Assert.Single(found);
    }

    [Fact]
    public void An_exact_name_match_clears_the_default_threshold()
    {
        // The second silent failure: dividing by three put an exact match at 0.2, below the
        // 0.5 default, so every candidate was filtered out.
        var found = LinkCandidates.Find(
        [
            P("a", "Alex Barthorpe", "linkedin"),
            P("b", "alex.barthorpe", "instagram"),
        ]);

        Assert.Single(found);
        Assert.True(found[0].Score >= 0.5, $"score was {found[0].Score}");
    }

    [Fact]
    public void Two_records_with_the_same_platforms_are_not_offered()
    {
        // Linking them gains nothing, and a shared platform is evidence they are distinct
        // records rather than a missed link.
        var found = LinkCandidates.Find(
        [
            P("a", "Alex Barthorpe", "linkedin"),
            P("b", "alex.barthorpe", "linkedin"),
        ]);

        Assert.Empty(found);
    }

    [Fact]
    public void Different_names_are_not_offered()
    {
        var found = LinkCandidates.Find(
        [
            P("a", "Alex Barthorpe", "linkedin"),
            P("b", "Jordan Smith", "instagram"),
        ]);

        Assert.Empty(found);
    }

    [Fact]
    public void A_shared_phone_is_the_strongest_evidence()
    {
        var left = P("a", "Alex Barthorpe", "linkedin") with { Phones = ["+1 555 0701"] };
        var right = P("b", "A. Barthorpe", "instagram") with { Phones = ["+1 555 0701"] };

        var found = LinkCandidates.Find([left, right]);

        Assert.Single(found);
        Assert.Contains(found[0].Evidence, e => e.Kind == LinkEvidenceKind.SharedPhone);
        Assert.True(found[0].Score >= 0.9, $"score was {found[0].Score}");
    }

    [Fact]
    public void A_shared_email_is_strong_evidence()
    {
        var left = P("a", "Alex B", "linkedin") with { Emails = ["alex@example.invalid"] };
        var right = P("b", "alexb", "instagram") with { Emails = ["alex@example.invalid"] };

        var found = LinkCandidates.Find([left, right]);

        Assert.Single(found);
        Assert.Contains(found[0].Evidence, e => e.Kind == LinkEvidenceKind.SharedEmail);
    }

    [Fact]
    public void A_middle_initial_does_not_prevent_a_match()
    {
        // A real pair in this dataset: "Amit Phadnis" and "Amit S Phadnis".
        var found = LinkCandidates.Find(
        [
            P("amit-phadnis", "Amit Phadnis", "vcard"),
            P("amit-s-phadnis", "Amit S Phadnis", "linkedin"),
        ],
        minimumScore: 0.3);

        Assert.Single(found);
    }

    [Fact]
    public void Results_are_ordered_by_confidence()
    {
        var people = new List<Person>
        {
            P("a", "Alex Barthorpe", "linkedin"),
            P("b", "alex.barthorpe", "instagram"),
            P("c", "Jordan Smith", "vcard") with { Phones = ["+1 555 0702"] },
            P("d", "jordansmith", "instagram") with { Phones = ["+1 555 0702"] },
        };

        var found = LinkCandidates.Find(people);

        Assert.Equal(2, found.Count);
        // The pair sharing a phone outranks the pair sharing only a name.
        Assert.Contains(found[0].Evidence, e => e.Kind == LinkEvidenceKind.SharedPhone);
    }

    [Fact]
    public void A_single_person_produces_nothing()
    {
        Assert.Empty(LinkCandidates.Find([P("a", "Alex Barthorpe", "linkedin")]));
    }

    [Fact]
    public void Short_names_are_not_offered_as_candidates()
    {
        // "al" and "al" would otherwise collide on every two-letter name.
        Assert.Empty(LinkCandidates.Find(
        [
            P("a", "Al", "linkedin"),
            P("b", "al", "instagram"),
        ]));
    }

    [Fact]
    public void The_combined_summary_names_both_sides()
    {
        var found = LinkCandidates.Find(
        [
            P("a", "Alex Barthorpe", "linkedin"),
            P("b", "alex.barthorpe", "instagram"),
        ]);

        Assert.Contains("linkedin", found[0].PlatformSummary);
        Assert.Contains("instagram", found[0].PlatformSummary);
    }
}
