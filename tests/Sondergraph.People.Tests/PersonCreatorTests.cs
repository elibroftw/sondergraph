using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.People.Tests;

/// <summary>
/// Tests for creating a contact by hand.
/// </summary>
/// <remarks>
/// This is the only path that produces a person without an export behind it, so the risks are
/// different from ingest: there is no source record to fall back on, and a typo is permanent
/// unless the user notices it. The validation tests matter as much as the happy path.
/// </remarks>
public sealed class PersonCreatorTests : IDisposable
{
    private readonly string _root;
    private readonly PersonStore _store;
    private readonly PersonCreator _creator;

    public PersonCreatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sg-create-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(_root);

        var created = Workspace.Create(_root);
        Assert.True(created.IsOk, created.Message);

        _store = new PersonStore(created.ValueOrThrow());
        _creator = new PersonCreator(_store);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void A_name_alone_is_enough()
    {
        var result = _creator.Create("Ada Lovelace");

        Assert.True(result.IsOk, result.Message);

        var person = result.ValueOrThrow();
        Assert.Equal("Ada Lovelace", person.Name);
        Assert.Equal("ada-lovelace", person.Id);
        Assert.Empty(person.Phones);
        Assert.Empty(person.Emails);
    }

    [Fact]
    public void The_record_is_marked_manual_so_a_re_import_does_not_hide_it()
    {
        var person = _creator.Create("Ada Lovelace").ValueOrThrow();

        Assert.Contains(PersonCreator.ManualSource, person.Sources);
    }

    [Fact]
    public void Details_are_stored_as_typed()
    {
        var person = _creator
            .Create("Ada Lovelace", ["+1 555 0100"], ["ada@example.invalid"])
            .ValueOrThrow();

        Assert.Equal(["+1 555 0100"], person.Phones);
        Assert.Equal(["ada@example.invalid"], person.Emails);
    }

    [Fact]
    public void A_social_account_adds_its_platform_to_the_source_list()
    {
        // Otherwise the person would be filtered out of the very source they arrived from.
        var person = _creator
            .Create("Ada Lovelace", accounts: [PlatformAccount.Create("instagram", "ada")])
            .ValueOrThrow();

        Assert.Contains("instagram", person.Sources);
        Assert.Contains(PersonCreator.ManualSource, person.Sources);
        Assert.Single(person.Accounts);
    }

    [Fact]
    public void A_blank_name_is_refused()
    {
        Assert.False(_creator.Create("   ").IsOk);
        Assert.False(_creator.Create(null).IsOk);
    }

    [Fact]
    public void An_unusable_phone_is_dropped_rather_than_stored()
    {
        // "451-90" is an extension, and a string with no digits is not a number at all. Storing
        // either would present a number that cannot be dialled.
        var person = _creator.Create("Ada Lovelace", ["451-90", "", "nonsense"]).ValueOrThrow();

        Assert.Empty(person.Phones);
    }

    [Fact]
    public void A_malformed_email_is_dropped()
    {
        var person = _creator.Create("Ada Lovelace", emails: ["notanemail", "also@wrong"]).ValueOrThrow();

        Assert.Empty(person.Emails);
    }

    [Fact]
    public void The_same_number_in_two_formats_is_stored_once()
    {
        var person = _creator
            .Create("Ada Lovelace", ["+1 555 0100", "+15550100"])
            .ValueOrThrow();

        Assert.Single(person.Phones);
    }

    [Fact]
    public void Two_people_with_the_same_name_get_different_ids()
    {
        var first = _creator.Create("Alex Chen").ValueOrThrow();
        var second = _creator.Create("Alex Chen").ValueOrThrow();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _store.ListAll(out _).ValueOrThrow().Count);
    }

    [Fact]
    public void The_created_person_can_be_reloaded()
    {
        var created = _creator
            .Create("Ada Lovelace", ["+1 555 0100"], ["ada@example.invalid"])
            .ValueOrThrow();

        var reopened = Workspace.Create(_root);
        Assert.True(reopened.IsOk, reopened.Message);

        var reloaded = new PersonStore(reopened.ValueOrThrow()).Load(created.Id).ValueOrThrow();

        Assert.Equal("Ada Lovelace", reloaded.Name);
        Assert.Equal(["+1 555 0100"], reloaded.Phones);
        Assert.Contains(PersonCreator.ManualSource, reloaded.Sources);
    }
}
