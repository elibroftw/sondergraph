using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.People.Tests;

/// <summary>
/// Tests for deleting a contact.
/// </summary>
/// <remarks>
/// The risk is not deleting the right file - that is one call. It is what the rest of the
/// workspace is left pointing at afterwards, and whether the deletion survives the next import.
/// </remarks>
public sealed class PersonDeleterTests : IDisposable
{
    private readonly string _root;
    private readonly PersonStore _store;
    private readonly PersonEditor _editor;
    private readonly DeletionLog _log;
    private readonly PersonDeleter _deleter;

    public PersonDeleterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sg-delete-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(_root);

        var created = Workspace.Create(_root);
        Assert.True(created.IsOk, created.Message);

        _store = new PersonStore(created.ValueOrThrow());
        _editor = new PersonEditor(_store);

        var log = DeletionLog.Load(_root);
        Assert.True(log.IsOk, log.Message);
        _log = log.ValueOrThrow();

        _deleter = new PersonDeleter(_store, _log);

        foreach (var (id, name) in new[] { ("ada", "Ada"), ("bo", "Bo"), ("cy", "Cy") })
        {
            Assert.True(_store.Save(new Person
            {
                Id = id,
                Name = name,
                CreatedAt = DateOnly.FromDateTime(DateTime.UtcNow),
            }).IsOk);
        }
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
    public void The_record_is_removed()
    {
        var result = _deleter.Delete("bo");

        Assert.True(result.IsOk, result.Message);
        Assert.False(_store.Exists("bo"));
        Assert.DoesNotContain(_store.ListAll(out _).ValueOrThrow(), p => p.Id == "bo");
    }

    [Fact]
    public void The_deletion_is_remembered_with_the_whole_document()
    {
        Assert.True(_deleter.Delete("bo").IsOk);

        var record = _log.Records.Single();

        Assert.Equal("bo", record.Id);
        Assert.Equal("Bo", record.Name);
        Assert.Contains("Bo", record.Document);
    }

    [Fact]
    public void References_in_other_contacts_are_removed()
    {
        // A file left holding a deleted person's id would be a dangling pointer: an edge in the
        // graph that leads nowhere, and an id rendered where a name should be.
        Assert.True(_editor.AddFriend("ada", "bo").IsOk);
        Assert.True(_editor.AddFriend("cy", "bo").IsOk);
        Assert.True(_editor.SetKnownThrough("cy", "bo").IsOk);

        var result = _deleter.Delete("bo").ValueOrThrow();

        Assert.Equal(2, result.ClearedMutuals);
        Assert.Equal(1, result.ClearedIntroducers);

        Assert.Empty(_store.Load("ada").ValueOrThrow().FriendsWith);
        Assert.Empty(_store.Load("cy").ValueOrThrow().FriendsWith);
        Assert.Null(_store.Load("cy").ValueOrThrow().KnownThrough);
    }

    [Fact]
    public void Unrelated_data_is_left_alone()
    {
        Assert.True(_editor.AddFriend("ada", "cy").IsOk);
        Assert.True(_editor.AddFriend("cy", "ada").IsOk);

        Assert.True(_deleter.Delete("bo").IsOk);

        Assert.Equal(["cy"], _store.Load("ada").ValueOrThrow().FriendsWith);
        Assert.Equal(["ada"], _store.Load("cy").ValueOrThrow().FriendsWith);
    }

    [Fact]
    public void The_deletion_survives_a_reload()
    {
        Assert.True(_deleter.Delete("bo").IsOk);

        var reopened = DeletionLog.Load(_root);
        Assert.True(reopened.IsOk, reopened.Message);

        Assert.True(reopened.ValueOrThrow().Contains("bo"));
    }

    [Fact]
    public void A_missing_person_is_reported_rather_than_ignored()
    {
        Assert.False(_deleter.Delete("nobody").IsOk);
        Assert.Empty(_log.Records);
    }

    [Fact]
    public void A_blank_id_is_refused()
    {
        Assert.False(_deleter.Delete("").IsOk);
    }
}
