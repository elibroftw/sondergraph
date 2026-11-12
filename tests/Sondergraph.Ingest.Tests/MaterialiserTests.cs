using Sondergraph.Core;
using Sondergraph.Ingest;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.Ingest.Tests;

/// <summary>
/// Ingest-side tests for <see cref="Materialiser"/>: what happens to records that arrive with
/// no strong identifier in common with anyone already stored.
/// </summary>
/// <remarks>
/// Fixtures are synthetic - invented names, and phone numbers in the reserved
/// <c>+1 555 01xx</c> range - but the shape is taken from a real incident, described on the
/// regression test below.
/// </remarks>
public sealed class MaterialiserTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sondergraph-materialiser-tests",
        Guid.NewGuid().ToString("N"));

    private PersonStore NewStore()
    {
        var created = Workspace.Create(_root);
        Assert.True(created.IsOk, created.Message);
        return new PersonStore(created.ValueOrThrow());
    }

    /// <summary>A store over a fresh temporary workspace, plus a log rooted at the same place.</summary>
    private (PersonStore Store, MergeLog Log) NewStoreAndLog()
    {
        var store = NewStore();
        var loaded = MergeLog.Load(_root);
        Assert.True(loaded.IsOk, loaded.Message);
        return (store, loaded.ValueOrThrow());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ObservedAccount Account(
        string platform,
        string handle,
        string? displayName,
        IReadOnlyList<string>? phones = null,
        IReadOnlyList<string>? emails = null,
        string? profileUrl = null) => new()
        {
            NativeId = $"{platform}:{handle}",
            SourcePath = $"{platform}.csv",
            Platform = platform,
            Handle = handle,
            DisplayName = displayName,
            Phones = phones ?? [],
            Emails = emails ?? [],
            ProfileUrl = profileUrl,
        };

    [Fact]
    public async Task Enrichment_adds_a_platform_without_losing_contact_details()
    {
        var (store, log) = NewStoreAndLog();
        var seed = new Person
        {
            Id = "robin-example",
            Name = "Robin Example",
            Phones = ["+1 555 0100"],
            Emails = ["robin@example.invalid"],
            Accounts =
            [
                PlatformAccount.Create(
                    "vcard",
                    "Robin Example",
                    "Robin Example",
                    null,
                    new DateOnly(2026, 1, 1)),
            ],
            Sources = ["vcard"],
        };
        Assert.True(store.Save(seed).IsOk);

        // A second platform arrives carrying a profile URL that the first did not have.
        var phoneOnly = Account(
            "contacts",
            "Robin Example",
            "Robin Example",
            phones: [],
            emails: []);
        var record = Account(
            "exampleapp",
            "Robin Ex",
            "Robin Example",
            profileUrl: "https://example.invalid/in/robin-example");

        var materialiser = new Materialiser(store, log);
        var report = await materialiser.MaterialiseAsync([phoneOnly, record]);

        Assert.True(report.IsOk, report.Message);

        var stored = store.Load("robin-example").ValueOrThrow();
        Assert.Equal(["+1 555 0100"], stored.Phones);
        Assert.Equal(["robin@example.invalid"], stored.Emails);
        Assert.Contains("vcard", stored.Sources);
        Assert.Contains("exampleapp", stored.Sources);
    }

    [Fact]
    public async Task An_incoming_record_that_only_shares_a_name_does_not_wipe_the_stored_record()
    {
        // Regression. A LinkedIn export writes the person's *name* as the account handle, so
        // the derived id collides with an existing vCard record while sharing no strong
        // identifier (no phone, no email, no profile URL on the vCard side).
        //
        // AllocateId reuses the existing id when the names match - that is what keeps
        // re-ingest idempotent. The materialiser then treated it as a new person and wrote a
        // freshly built document over the top, so the stored phone number, email and every
        // other account were deleted. Nine real contacts lost data this way.
        var (store, log) = NewStoreAndLog();
        var seed = new Person
        {
            Id = "robin-example",
            Name = "Robin Example",
            Phones = ["+1 555 0111"],
            Emails = ["robin@example.invalid"],
            Accounts =
            [
                PlatformAccount.Create(
                    "vcard",
                    "Robin Example",
                    "Robin Example",
                    null,
                    new DateOnly(2026, 1, 1)),
                PlatformAccount.Create(
                    "chat",
                    "robin-example",
                    "Robin Example",
                    null,
                    new DateOnly(2026, 1, 1)),
            ],
            Sources = ["vcard", "chat"],
        };
        Assert.True(store.Save(seed).IsOk);

        var record = Account(
            "worknet",
            "Robin Example",
            "Robin Example",
            profileUrl: "https://worknet.invalid/in/robin-example-060b7b188");

        var materialiser = new Materialiser(store, log);
        var report = await materialiser.MaterialiseAsync([record]);

        Assert.True(report.IsOk, report.Message);

        var stored = store.Load("robin-example").ValueOrThrow();

        // The whole point: nothing the record already knew is gone.
        Assert.Equal(["+1 555 0111"], stored.Phones);
        Assert.Equal(["robin@example.invalid"], stored.Emails);
        Assert.Contains("vcard", stored.Sources);
        Assert.Contains("chat", stored.Sources);
        Assert.Contains("worknet", stored.Sources);
        Assert.Contains(stored.Accounts, a => a.Platform == "chat");
        Assert.Contains(stored.Accounts, a => a.Platform == "worknet");
    }

    [Fact]
    public async Task A_profile_less_record_enriches_an_existing_person_but_creates_none()
    {
        // A Facebook friend list is a pile of bare names. They must be able to fill in a
        // contact that arrived from a phone book, and must never become records of their own -
        // creating a person per name produced 308 directories holding a name and no source.
        var (store, log) = NewStoreAndLog();
        Assert.True(store.Save(new Person
        {
            Id = "sample-person",
            Name = "Sample Person",
            Phones = ["+1 555 0122"],
            Accounts =
            [
                PlatformAccount.Create(
                    "vcard",
                    "Sample Person",
                    "Sample Person",
                    null,
                    new DateOnly(2026, 1, 1)),
            ],
            Sources = ["vcard"],
        }).IsOk);

        var materialiser = new Materialiser(store, log);
        var report = await materialiser.MaterialiseAsync(
        [
            new ObservedAccount
            {
                NativeId = "name:sample-person",
                SourcePath = "friends.json",
                Platform = "vcard",
                Handle = "Sample Person",
                DisplayName = "Sample Person",
                HasProfile = false,
                AddsSource = false,
            },
            new ObservedAccount
            {
                NativeId = "name:unknown-person",
                SourcePath = "friends.json",
                Platform = "facebookapp",
                Handle = "Unknown Person",
                DisplayName = "Unknown Person",
                HasProfile = false,
                AddsSource = false,
            },
        ]);

        Assert.True(report.IsOk, report.Message);

        var result = report.ValueOrThrow();
        Assert.Equal(0, result.PeopleCreated);
        Assert.Equal(1, result.ProfilelessSkipped);
        Assert.Equal(1, result.PeopleUpdated);

        var loaded = store.ListAll(out _).ValueOrThrow();
        Assert.Single(loaded);

        // Enriched, but still not claiming a platform it has no profile for.
        Assert.Equal(["+1 555 0122"], loaded[0].Phones);
        Assert.Equal(["vcard"], loaded[0].Sources);
    }

    [Fact]
    public async Task A_deleted_person_is_not_recreated_by_a_later_ingest()
    {
        // Every export still contains the person. Without this, deleting is temporary and the
        // user removes the same contact after every import.
        var store = NewStore();
        var log = MergeLog.Load(_root).ValueOrThrow();
        var deletions = DeletionLog.Load(_root).ValueOrThrow();

        var record = Account(
            "worknet",
            "Gone Person",
            "Gone Person",
            profileUrl: "https://worknet.invalid/in/gone-person");

        var first = await new Materialiser(store, log, deletions).MaterialiseAsync([record]);
        Assert.True(first.IsOk, first.Message);
        Assert.Equal(1, first.ValueOrThrow().PeopleCreated);

        var id = store.ListAll(out _).ValueOrThrow().Single().Id;

        var deleted = new PersonDeleter(store, deletions).Delete(id);
        Assert.True(deleted.IsOk, deleted.Message);

        // Reload the log as a fresh ingest run would.
        var reloaded = DeletionLog.Load(_root).ValueOrThrow();
        var second = await new Materialiser(store, log, reloaded)
            .MaterialiseAsync([record]);

        Assert.True(second.IsOk, second.Message);
        Assert.Equal(0, second.ValueOrThrow().PeopleCreated);
        Assert.Equal(1, second.ValueOrThrow().DeletedSkipped);
        Assert.Empty(store.ListAll(out _).ValueOrThrow());
    }

    [Fact]
    public async Task Ingesting_the_same_record_twice_is_idempotent()
    {
        var (store, log) = NewStoreAndLog();
        var record = Account(
            "worknet",
            "Alex Sample",
            "Alex Sample",
            profileUrl: "https://worknet.invalid/in/alex-sample");

        var materialiser = new Materialiser(store, log);
        var first = await materialiser.MaterialiseAsync([record]);
        var second = await materialiser.MaterialiseAsync([record]);

        Assert.True(first.IsOk, first.Message);
        Assert.True(second.IsOk, second.Message);

        var loaded = store.ListAll(out _).ValueOrThrow();
        Assert.Single(loaded);
        Assert.Equal(0, second.ValueOrThrow().PeopleCreated);
    }
}
