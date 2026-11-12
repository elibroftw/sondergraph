using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.People.Tests;

/// <summary>
/// Tests for following merges, including chains of them.
/// </summary>
/// <remarks>
/// A chain is ordinary: merge A into B, then later realise B is also C. Resolution has to land
/// on the end of the chain, because a caller told "A survived as B" will try to load B and
/// find a tombstone.
/// </remarks>
public sealed class MergeLogTests : IDisposable
{
    private readonly string _root;
    private readonly MergeLog _log;

    public MergeLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sg-mergelog-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(_root);

        var created = Workspace.Create(_root);
        Assert.True(created.IsOk, created.Message);

        var loaded = MergeLog.Load(_root);
        Assert.True(loaded.IsOk, loaded.Message);
        _log = loaded.ValueOrThrow();
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

    private void Merge(string survivor, string absorbed) =>
        Assert.True(
            _log.Append(new MergeRecord
            {
                Id = Guid.NewGuid().ToString("n")[..8],
                At = DateTimeOffset.UtcNow,
                SurvivorId = survivor,
                AbsorbedId = absorbed,
                AbsorbedDocument = "---\n---\n",
                SurvivorDocumentBefore = "---\n---\n",
            }).IsOk);

    [Fact]
    public void An_unmerged_person_resolves_to_nothing()
    {
        Assert.Null(_log.FindAbsorbing("someone"));
    }

    [Fact]
    public void A_single_merge_is_followed()
    {
        Merge("survivor", "absorbed");

        Assert.Equal("survivor", _log.FindAbsorbing("absorbed")?.SurvivorId);
    }

    [Fact]
    public void A_chain_of_merges_resolves_to_the_end()
    {
        // The real case: a vCard contact with a phone was merged into an Instagram record,
        // which was then merged into a third. Ingest that looked up the vCard id was told
        // "the survivor is the Instagram record" - which had itself been absorbed - so the
        // phone number never reached the person who actually survived.
        Merge("middle", "original");
        Merge("final", "middle");

        Assert.Equal("final", _log.FindAbsorbing("original")?.SurvivorId);
        Assert.Equal("final", _log.FindAbsorbing("middle")?.SurvivorId);
    }

    [Fact]
    public void A_three_hop_chain_still_resolves_to_the_end()
    {
        Merge("b", "a");
        Merge("c", "b");
        Merge("d", "c");

        Assert.Equal("d", _log.FindAbsorbing("a")?.SurvivorId);
    }

    [Fact]
    public void A_reversed_merge_is_not_followed()
    {
        Merge("survivor", "absorbed");

        var only = _log.Active.First();
        Assert.True(_log.RecordUndo(only.Id).IsOk);

        // The undo is recorded as its own entry, so the original merge is no longer active.
        Assert.Null(_log.FindAbsorbing("absorbed"));
    }
}
