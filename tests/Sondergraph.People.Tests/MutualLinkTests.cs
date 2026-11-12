using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.People.Tests;

/// <summary>
/// Tests for mutual links, which are one-directional.
/// </summary>
/// <remarks>
/// These used to be written to both records at once, so a mutual was symmetric by
/// construction. It now records a claim by one person about another: linking writes only the
/// listing record, and the two sides can disagree.
/// </remarks>
public sealed class MutualLinkTests : IDisposable
{
    private readonly string _root;
    private readonly PersonStore _store;
    private readonly PersonEditor _editor;

    public MutualLinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sg-mutual-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(_root);

        var created = Workspace.Create(_root);
        Assert.True(created.IsOk, created.Message);

        _store = new PersonStore(created.ValueOrThrow());
        _editor = new PersonEditor(_store);

        foreach (var (id, name) in new[] { ("ada", "Ada"), ("bo", "Bo") })
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
    public void Linking_writes_only_the_listing_record()
    {
        Assert.True(_editor.AddFriend("ada", "bo").IsOk);

        Assert.Equal(["bo"], _store.Load("ada").ValueOrThrow().FriendsWith);
        Assert.Empty(_store.Load("bo").ValueOrThrow().FriendsWith);
    }

    [Fact]
    public void Both_sides_linking_makes_a_reciprocal_pair()
    {
        Assert.True(_editor.AddFriend("ada", "bo").IsOk);
        Assert.True(_editor.AddFriend("bo", "ada").IsOk);

        Assert.Equal(["bo"], _store.Load("ada").ValueOrThrow().FriendsWith);
        Assert.Equal(["ada"], _store.Load("bo").ValueOrThrow().FriendsWith);
    }

    [Fact]
    public void Linking_twice_does_not_duplicate()
    {
        Assert.True(_editor.AddFriend("ada", "bo").IsOk);
        Assert.True(_editor.AddFriend("ada", "bo").IsOk);

        Assert.Single(_store.Load("ada").ValueOrThrow().FriendsWith);
    }

    [Fact]
    public void Unlinking_leaves_the_other_side_alone()
    {
        // Taking back your own claim is not an edit to someone else's record.
        Assert.True(_editor.AddFriend("ada", "bo").IsOk);
        Assert.True(_editor.AddFriend("bo", "ada").IsOk);

        Assert.True(_editor.RemoveFriend("ada", "bo").IsOk);

        Assert.Empty(_store.Load("ada").ValueOrThrow().FriendsWith);
        Assert.Equal(["ada"], _store.Load("bo").ValueOrThrow().FriendsWith);
    }

    [Fact]
    public void A_self_link_is_refused()
    {
        Assert.False(_editor.AddFriend("ada", "ada").IsOk);
    }

    [Fact]
    public void Linking_a_missing_person_fails()
    {
        Assert.False(_editor.AddFriend("ada", "nobody").IsOk);
        Assert.Empty(_store.Load("ada").ValueOrThrow().FriendsWith);
    }

    [Fact]
    public void Recording_an_introducer_persists_to_the_document()
    {
        Assert.True(_editor.SetKnownThrough("bo", "ada").IsOk);

        Assert.Equal("ada", _store.Load("bo").ValueOrThrow().KnownThrough);

        // Round-trips through the file, not just memory.
        var reopened = Workspace.Create(_root);
        Assert.True(reopened.IsOk, reopened.Message);

        var reloaded = new PersonStore(reopened.ValueOrThrow()).Load("bo").ValueOrThrow();
        Assert.Equal("ada", reloaded.KnownThrough);
    }

    [Fact]
    public void The_introducer_is_stored_only_on_the_introduced_person()
    {
        // "I know Bo through Ada" is a fact about Bo. Writing it onto Ada would mean Ada's
        // record asserting something about someone else.
        Assert.True(_editor.SetKnownThrough("bo", "ada").IsOk);

        Assert.Null(_store.Load("ada").ValueOrThrow().KnownThrough);
        Assert.Empty(_store.Load("ada").ValueOrThrow().FriendsWith);
    }

    [Fact]
    public void Clearing_the_introducer_leaves_nobody()
    {
        Assert.True(_editor.SetKnownThrough("bo", "ada").IsOk);
        Assert.True(_editor.SetKnownThrough("bo", null).IsOk);

        Assert.Null(_store.Load("bo").ValueOrThrow().KnownThrough);
    }

    [Fact]
    public void A_person_cannot_be_introduced_by_themselves()
    {
        Assert.False(_editor.SetKnownThrough("bo", "bo").IsOk);
    }

    [Fact]
    public void Naming_an_unknown_introducer_is_refused()
    {
        // Otherwise the record would point at a person who does not exist, and the panel would
        // render a dangling id where a name should be.
        Assert.False(_editor.SetKnownThrough("bo", "nobody").IsOk);
        Assert.Null(_store.Load("bo").ValueOrThrow().KnownThrough);
    }

    [Fact]
    public void Being_introduced_and_being_mutual_are_independent()
    {
        Assert.True(_editor.SetKnownThrough("bo", "ada").IsOk);
        Assert.True(_editor.AddFriend("bo", "ada").IsOk);

        var bo = _store.Load("bo").ValueOrThrow();

        Assert.Equal("ada", bo.KnownThrough);
        Assert.Equal(["ada"], bo.FriendsWith);
    }
}
