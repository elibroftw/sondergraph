using Sondergraph.Core;

namespace Sondergraph.People.Tests;

/// <summary>
/// Tests for editing the human-owned fields: tier, tags, locations, name.
/// </summary>
/// <remarks>
/// The central risk here is not corruption but <em>loss</em>: these are the fields a person
/// typed by hand, and the design guarantee is that ingest never overwrites them. Several of
/// these tests exist to pin that guarantee rather than the mechanics of setting a value.
/// </remarks>
public sealed class PersonEditorTests : IDisposable
{
    private readonly string _root;
    private readonly PersonStore _store;
    private readonly PersonEditor _editor;

    public PersonEditorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sg-edit-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(_root);

        var created = Sondergraph.Workspaces.Workspace.Create(_root);
        Assert.True(created.IsOk, created.Message);

        _store = new PersonStore(created.ValueOrThrow());
        _editor = new PersonEditor(_store);

        _store.Save(new Person
        {
            Id = "example-person",
            Name = "Example Person",
            Phones = ["+1 555 0301"],
            CreatedAt = DateOnly.FromDateTime(DateTime.UtcNow),
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

    private Person Load() => _store.Load("example-person").ValueOrThrow();

    [Fact]
    public void Sets_the_relationship_tier()
    {
        Assert.True(_editor.SetTier("example-person", ContactTier.Close).IsOk);

        Assert.Equal(ContactTier.Close, Load().Tier);
    }

    [Fact]
    public void Tier_survives_a_reload_from_disk()
    {
        // The tier is written to YAML and must parse back, including the "close" wire form
        // that does not match the enum name.
        _editor.SetTier("example-person", ContactTier.Close);

        var reloaded = _store.Load("example-person").ValueOrThrow();

        Assert.Equal(ContactTier.Close, reloaded.Tier);
    }

    [Fact]
    public void Adds_and_normalises_a_tag()
    {
        Assert.True(_editor.AddTag("example-person", "Big Client").IsOk);

        Assert.Equal(["big-client"], Load().Tags);
    }

    [Fact]
    public void Does_not_add_a_duplicate_tag_in_any_case()
    {
        _editor.AddTag("example-person", "family");
        _editor.AddTag("example-person", "Family");
        _editor.AddTag("example-person", "  FAMILY  ");

        Assert.Single(Load().Tags);
    }

    [Fact]
    public void Keeps_tags_sorted()
    {
        // Unsorted tags would produce a different file depending on the order they were
        // added, which shows up as a spurious git diff.
        _editor.AddTag("example-person", "zeta");
        _editor.AddTag("example-person", "alpha");
        _editor.AddTag("example-person", "Mike");

        Assert.Equal(["alpha", "mike", "zeta"], Load().Tags);
    }

    [Fact]
    public void Removes_a_tag_case_insensitively()
    {
        _editor.AddTag("example-person", "conference");
        Assert.True(_editor.RemoveTag("example-person", "CONFERENCE").IsOk);

        Assert.Empty(Load().Tags);
    }

    [Fact]
    public void Removing_an_absent_tag_is_not_an_error()
    {
        // Making this fail would force every caller to check first for no benefit.
        Assert.True(_editor.RemoveTag("example-person", "nonexistent").IsOk);
    }

    [Fact]
    public void Sets_tags_outright()
    {
        _editor.AddTag("example-person", "old");

        Assert.True(_editor.SetTags("example-person", ["beta", "alpha", "beta", "  "]).IsOk);

        Assert.Equal(["alpha", "beta"], Load().Tags);
    }

    [Fact]
    public void Adds_a_location_with_a_kind()
    {
        Assert.True(_editor.AddLocation("example-person", "Toronto", LocationKind.Work).IsOk);

        var location = Assert.Single(Load().Locations);
        Assert.Equal("Toronto", location.Place);
        Assert.Equal(LocationKind.Work, location.Kind);
    }

    [Fact]
    public void Adding_the_same_place_and_kind_updates_rather_than_duplicates()
    {
        _editor.AddLocation("example-person", "Toronto", LocationKind.Work, "first floor");
        _editor.AddLocation("example-person", "Toronto", LocationKind.Work, "third floor");

        var location = Assert.Single(Load().Locations);
        Assert.Equal("third floor", location.Note);
    }

    [Fact]
    public void The_same_place_can_be_recorded_with_two_kinds()
    {
        // Living and working in the same city is ordinary and must not collapse.
        _editor.AddLocation("example-person", "Toronto", LocationKind.Home);
        _editor.AddLocation("example-person", "Toronto", LocationKind.Work);

        Assert.Equal(2, Load().Locations.Count);
    }

    [Fact]
    public void Removes_a_location()
    {
        _editor.AddLocation("example-person", "Toronto", LocationKind.Home);
        Assert.True(_editor.RemoveLocation("example-person", "toronto").IsOk);

        Assert.Empty(Load().Locations);
    }

    [Fact]
    public void Rejects_an_empty_location()
    {
        Assert.False(_editor.AddLocation("example-person", "   ").IsOk);
    }

    [Fact]
    public void Renames_without_changing_the_id()
    {
        // The id is the directory name that notes, media and merge records point at.
        // Changing it would orphan all of them.
        Assert.True(_editor.Rename("example-person", "A Better Name").IsOk);

        var person = Load();
        Assert.Equal("A Better Name", person.Name);
        Assert.Equal("example-person", person.Id);
    }

    [Fact]
    public void Edits_preserve_data_written_by_ingest()
    {
        // The core guarantee of the split: setting a tier must not drop phones or accounts.
        Assert.True(_editor.SetTier("example-person", ContactTier.Friend).IsOk);

        var person = Load();
        Assert.Equal(["+1 555 0301"], person.Phones);
    }

    [Fact]
    public void Every_edit_preserves_the_other_human_fields()
    {
        _editor.SetTier("example-person", ContactTier.Best);
        _editor.AddTag("example-person", "keep-me");
        _editor.AddLocation("example-person", "Oslo", LocationKind.Home);

        // Change one field and check the others are still there.
        _editor.AddTag("example-person", "another");

        var person = Load();
        Assert.Equal(ContactTier.Best, person.Tier);
        Assert.Contains("keep-me", person.Tags);
        Assert.Contains(person.Locations, l => l.Place == "Oslo");
    }

    [Fact]
    public void A_location_becomes_a_searchable_identifier()
    {
        // Recording where someone lives is only useful if "who do I know in Berlin?" then
        // works. Without this the location would be stored and unqueryable.
        _editor.AddLocation("example-person", "Berlin", LocationKind.Home);

        var found = Load().AllIdentifiers()
            .Any(i => i.Contains("Berlin", StringComparison.OrdinalIgnoreCase));

        Assert.True(found, "a saved location should appear in the searchable identifiers");
    }

    [Fact]
    public void A_location_note_is_searchable_too()
    {
        _editor.AddLocation("example-person", "London", LocationKind.Work, "the Shard");

        Assert.Contains(Load().AllIdentifiers(), i => i.Contains("Shard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Locations_appear_in_the_display_summary()
    {
        _editor.AddLocation("example-person", "Oslo", LocationKind.Home);

        var summary = PersonDocument.Describe(Load());

        // The place name, not a count: "Oslo" is informative, "1 location" is not.
        Assert.Contains("Oslo", summary);
    }

    [Fact]
    public void Unknown_person_fails_without_creating_one()
    {
        // A typo in an id must not invent a contact.
        var result = _editor.SetTier("no-such-person", ContactTier.Friend);

        Assert.False(result.IsOk);
        Assert.Equal(ErrorCode.WorkspaceNotFound, result.Code);
    }

    [Fact]
    public void Edits_stamp_an_update_time()
    {
        // UpdatedAt is a DateOnly, so it cannot advance within one test run. Asserting that
        // it changes would be asserting something the type cannot express; what matters is
        // that an edit sets it rather than leaving it null.
        Assert.True(_editor.SetTier("example-person", ContactTier.Friend).IsOk);

        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), Load().UpdatedAt);
    }

    [Fact]
    public void Marking_a_person_as_self_persists_to_the_document()
    {
        var result = _editor.SetIsMe("example-person", true);
        Assert.True(result.IsOk, result.Message);

        var saved = _store.Load("example-person").ValueOrThrow();
        Assert.True(saved.IsMe);

        // Round-trips through the file, not just the in-memory record.
        var reopened = Sondergraph.Workspaces.Workspace.Create(_root);
        Assert.True(reopened.IsOk, reopened.Message);
        var reloaded = new PersonStore(reopened.ValueOrThrow())
            .Load("example-person")
            .ValueOrThrow();
        Assert.True(reloaded.IsMe);
    }

    [Fact]
    public void Only_one_person_can_be_self()
    {
        // Two "You" nodes would put two centres in a graph that has one.
        _store.Save(new Person
        {
            Id = "second-person",
            Name = "Second Person",
            CreatedAt = DateOnly.FromDateTime(DateTime.UtcNow),
        });

        Assert.True(_editor.SetIsMe("example-person", true).IsOk);
        Assert.True(_editor.SetIsMe("second-person", true).IsOk);

        var people = _store.ListAll(out _).ValueOrThrow();

        Assert.Single(people, p => p.IsMe);
        Assert.True(people.Single(p => p.Id == "second-person").IsMe);
        Assert.False(people.Single(p => p.Id == "example-person").IsMe);
    }

    [Fact]
    public void Clearing_the_marking_leaves_nobody_as_self()
    {
        Assert.True(_editor.SetIsMe("example-person", true).IsOk);
        Assert.True(_editor.SetIsMe("example-person", false).IsOk);

        Assert.DoesNotContain(_store.ListAll(out _).ValueOrThrow(), p => p.IsMe);
    }

    [Fact]
    public void A_person_marked_as_self_is_still_a_normal_contact()
    {
        // Self is not a suppression: the record stays in the list like anyone else.
        Assert.True(_editor.SetIsMe("example-person", true).IsOk);

        var people = _store.ListAll(out _).ValueOrThrow();

        Assert.Contains(people, p => p.Id == "example-person");
        Assert.Equal(["+1 555 0301"], people.Single(p => p.Id == "example-person").Phones);
    }
}
