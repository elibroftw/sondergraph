using Sondergraph.Core;

namespace Sondergraph.People.Tests;

/// <summary>
/// Tests for merging and unmerging.
/// </summary>
/// <remarks>
/// Merging is the riskiest operation in the system, so these tests are mostly about what must
/// NOT happen: losing a phone number, dropping a hand-written tag, or being unable to undo.
/// The mechanics of combining two records are the easy part.
/// </remarks>
public sealed class MergeServiceTests : IDisposable
{
    private readonly string _root;
    private readonly PersonStore _store;
    private readonly MergeService _service;

    public MergeServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sg-merge-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(_root);

        var created = Sondergraph.Workspaces.Workspace.Create(_root);
        _store = new PersonStore(created.ValueOrThrow());

        var log = MergeLog.Load(_root).ValueOrThrow();
        _service = new MergeService(_store, log);

        // The realistic case: the same person from two platforms, one with contact details
        // and one with a social handle.
        _store.Save(new Person
        {
            Id = "example-person",
            Name = "Example Person",
            Phones = ["+1 555 0401"],
            Emails = ["example@example.invalid"],
            Tier = ContactTier.Friend,
            Tags = ["family"],
            Sources = ["vcard", "facebook"],
            Accounts =
            [
                PlatformAccount.Create("vcard", "Example Person"),
                PlatformAccount.Create("facebook", "Example Person"),
            ],
            CreatedAt = new DateOnly(2026, 1, 1),
        });

        _store.Save(new Person
        {
            Id = "example-handle",
            Name = "example_handle",
            Tags = ["conference"],
            Locations = [new ContactLocation("Toronto", LocationKind.Home)],
            Sources = ["instagram"],
            Accounts = [PlatformAccount.Create("instagram", "example_handle")],
            CreatedAt = new DateOnly(2026, 6, 1),
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private Person Load(string id) => _store.Load(id).ValueOrThrow();

    [Fact]
    public void Merges_across_platforms_into_one_person()
    {
        // The headline case: an Instagram handle joined to a contacts entry.
        var merged = _service.Merge("example-person", "example-handle", "same person");

        Assert.True(merged.IsOk, merged.Message);

        var person = Load("example-person");
        Assert.Contains(person.Accounts, a => a.Platform == "instagram");
        Assert.Contains("instagram", person.Sources);
    }

    [Fact]
    public void The_absorbed_person_disappears_from_listings()
    {
        // Without this the merge would look like it did nothing: the directory is retained
        // for undo, but it must not be a separate contact.
        _service.Merge("example-person", "example-handle");

        var listed = _store.ListAll(out _).ValueOrThrow();

        Assert.Single(listed);
        Assert.Equal("example-person", listed[0].Id);
        Assert.Equal(1, _store.MergedAwayCount);
    }

    [Fact]
    public void Phone_numbers_from_both_sides_are_kept()
    {
        // Losing a phone number would be the worst possible outcome of a merge.
        _store.Save(Load("example-handle") with { Phones = ["+1 555 0402"] });

        _service.Merge("example-person", "example-handle");

        var person = Load("example-person");
        Assert.Equal(2, person.Phones.Count);
    }

    [Fact]
    public void Hand_written_tags_from_both_sides_are_kept()
    {
        _service.Merge("example-person", "example-handle");

        var person = Load("example-person");
        Assert.Contains("family", person.Tags);
        Assert.Contains("conference", person.Tags);
    }

    [Fact]
    public void The_survivors_relationship_rating_wins()
    {
        // The user already decided how close this person is; a merge must not downgrade it.
        _store.Save(Load("example-handle") with { Tier = ContactTier.Best });

        _service.Merge("example-person", "example-handle");

        Assert.Equal(ContactTier.Friend, Load("example-person").Tier);
    }

    [Fact]
    public void An_unrated_survivor_adopts_the_absorbed_rating()
    {
        // The other direction: rather than discarding a rating, take it.
        _store.Save(Load("example-person") with { Tier = ContactTier.Unknown });
        _store.Save(Load("example-handle") with { Tier = ContactTier.Close });

        _service.Merge("example-person", "example-handle");

        Assert.Equal(ContactTier.Close, Load("example-person").Tier);
    }

    [Fact]
    public void Locations_are_unioned()
    {
        _store.Save(Load("example-person") with
        {
            Locations = [new ContactLocation("Oslo", LocationKind.Work)],
        });

        _service.Merge("example-person", "example-handle");

        var places = Load("example-person").Locations.Select(l => l.Place).ToList();
        Assert.Contains("Oslo", places);
        Assert.Contains("Toronto", places);
    }

    [Fact]
    public void The_absorbed_name_becomes_an_alias()
    {
        // A search for either name must still find the person after the merge.
        _service.Merge("example-person", "example-handle");

        var person = Load("example-person");
        Assert.Contains(person.Aliases, a => a.Contains("handle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_earlier_creation_date_is_kept()
    {
        // The person existed since the first record, whichever was written first.
        _service.Merge("example-person", "example-handle");

        Assert.Equal(new DateOnly(2026, 1, 1), Load("example-person").CreatedAt);
    }

    [Fact]
    public void Merging_is_recorded_in_the_log()
    {
        var merged = _service.Merge("example-person", "example-handle", "same person");
        var record = merged.ValueOrThrow();

        Assert.Equal("example-person", record.SurvivorId);
        Assert.Equal("example-handle", record.AbsorbedId);
        Assert.Contains("Example Person", record.SurvivorDocumentBefore);
        Assert.Contains("example_handle", record.AbsorbedDocument);
    }

    [Fact]
    public void Undo_restores_both_people_exactly()
    {
        // The reversibility guarantee: the merge log stores both documents verbatim, so undo
        // is a restore rather than a reconstruction.
        var mergeId = _service.Merge("example-person", "example-handle").ValueOrThrow().Id;

        var restored = _service.Undo(mergeId);
        Assert.True(restored.IsOk, restored.Message);

        var survivor = Load("example-person");
        Assert.Equal(ContactTier.Friend, survivor.Tier);
        Assert.Single(survivor.Phones);
        Assert.DoesNotContain(survivor.Accounts, a => a.Platform == "instagram");

        // The absorbed person is a normal contact again.
        var absorbed = Load("example-handle");
        Assert.Equal("example-handle", absorbed.Id);
        Assert.Contains(absorbed.Accounts, a => a.Platform == "instagram");

        var listed = _store.ListAll(out _).ValueOrThrow();
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public void Merging_an_already_absorbed_person_is_refused()
    {
        _service.Merge("example-person", "example-handle");

        // The absorbed person no longer exists as a contact, so a second merge cannot find it.
        var again = _service.Merge("example-person", "example-handle");
        Assert.False(again.IsOk);
    }

    [Fact]
    public void Undo_twice_is_refused()
    {
        var mergeId = _service.Merge("example-person", "example-handle").ValueOrThrow().Id;

        Assert.True(_service.Undo(mergeId).IsOk);
        Assert.False(_service.Undo(mergeId).IsOk);
    }

    [Fact]
    public void Merging_a_person_into_themselves_is_refused()
    {
        Assert.False(_service.Merge("example-person", "example-person").IsOk);
    }

    [Fact]
    public void Merging_into_an_absorbed_person_is_refused()
    {
        // Otherwise a chain forms that undo cannot follow.
        _service.Merge("example-person", "example-handle");

        var second = _service.Merge("example-handle", "example-person");
        Assert.False(second.IsOk);
    }

    [Fact]
    public void An_unknown_person_is_reported()
    {
        Assert.False(_service.Merge("example-person", "no-such-person").IsOk);
    }

    [Fact]
    public void Notes_move_to_the_survivor()
    {
        // A merge must never lose writing. The absorbed notes are copied across so the
        // survivor carries everything.
        var notes = new NoteService(_store);
        Assert.True(notes.SaveMainNote("example-handle", "A note about this person.").IsOk);

        _service.Merge("example-person", "example-handle");

        var reloaded = _store.Load("example-person").ValueOrThrow();
        Assert.Contains(reloaded.Notes, n => n.Body.Contains("A note about this person", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_accounts_are_not_copied_twice()
    {
        // Both records name the same Facebook account; the merge must not produce two.
        _store.Save(Load("example-handle") with
        {
            Accounts = [.. Load("example-handle").Accounts, PlatformAccount.Create("facebook", "Example Person")],
        });

        _service.Merge("example-person", "example-handle");

        var facebook = Load("example-person").Accounts.Count(a => a.Platform == "facebook");
        Assert.Equal(1, facebook);
    }
}
