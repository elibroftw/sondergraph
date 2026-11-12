using Sondergraph.Core;
using Sondergraph.People;

namespace Sondergraph.Ingest;

/// <summary>
/// What happened when records were written into the model.
/// </summary>
public sealed record MaterialiseReport
{
    /// <summary>Records seen by the materialiser.</summary>
    public int RecordsSeen { get; init; }

    /// <summary>People created for the first time.</summary>
    public int PeopleCreated { get; init; }

    /// <summary>People that already existed and were enriched.</summary>
    public int PeopleUpdated { get; init; }

    /// <summary>Records absorbed into an existing person rather than creating a new one.</summary>
    public int RecordsMerged { get; init; }

    /// <summary>Records skipped because they carried no usable identity.</summary>
    public int RecordsSkipped { get; init; }

    /// <summary>
    /// Records skipped because they described no profile and matched nobody already stored.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RecordsSkipped"/> so the two are not conflated: a bare name
    /// from a friend list is not a broken record, it is a record with nothing to attach to.
    /// </remarks>
    public int ProfilelessSkipped { get; init; }

    /// <summary>Records skipped because the person they would create was deleted.</summary>
    public int DeletedSkipped { get; init; }

    /// <summary>
    /// Pairs that look like the same person but were not merged automatically.
    /// </summary>
    /// <remarks>
    /// The whole point of the design (DESIGN.md §4.3): a wrong automatic merge is worse than
    /// no merge. These are surfaced for the user to confirm.
    /// </remarks>
    public IReadOnlyList<MergeCandidate> Candidates { get; init; } = [];

    /// <summary>Problems recorded during parsing, carried through for reporting.</summary>
    public IReadOnlyList<ParseProblem> Problems { get; init; } = [];
}

/// <summary>
/// Two people that may be the same human.
/// </summary>
/// <param name="LeftId">One person's id.</param>
/// <param name="RightId">The other person's id.</param>
/// <param name="Reason">Why they look alike, phrased for a human.</param>
/// <param name="Confidence">How strong the evidence is, 0..1.</param>
public sealed record MergeCandidate(string LeftId, string RightId, string Reason, double Confidence);

/// <summary>
/// Turns parsed records into people on disk.
/// </summary>
/// <remarks>
/// <para>
/// This is the third pipeline stage from ADR 0003: shared, platform-agnostic, and the only
/// place identity resolution happens. Parsers emit observations; this decides what is a
/// person.
/// </para>
/// <para>
/// <b>It matches on strong identifiers only</b> — phone, email, profile URL. Name matching is
/// explicitly <em>not</em> used to merge, because 353 Facebook friends have nothing but a name
/// and two people really can share one. Name similarity produces <see cref="MergeCandidate"/>
/// entries instead, which the user confirms.
/// </para>
/// </remarks>
public sealed class Materialiser
{
    private readonly PersonStore _store;
    private readonly MergeLog _mergeLog;
    private readonly DeletionLog _deletionLog;

    /// <summary>Creates a materialiser over a workspace.</summary>
    /// <param name="store">The workspace's people.</param>
    /// <param name="mergeLog">Merge history, used to redirect absorbed identifiers.</param>
    /// <param name="deletionLog">
    /// Deleted contacts. Optional so existing callers keep working, and so a workspace with no
    /// deletions pays nothing; when absent nothing is filtered.
    /// </param>
    public Materialiser(PersonStore store, MergeLog mergeLog, DeletionLog? deletionLog = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mergeLog);
        _store = store;
        _mergeLog = mergeLog;
        _deletionLog = deletionLog ?? DeletionLog.Empty();
    }

    /// <summary>
    /// Writes records into the model, merging into existing people where identifiers match.
    /// </summary>
    /// <param name="records">Records from one or more parsers.</param>
    /// <param name="progress">Optional per-record progress callback.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<MaterialiseReport>> MaterialiseAsync(
        IEnumerable<ParsedRecord> records,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var list = records.ToList();
        var loaded = _store.ListAll(out var warnings);

        if (!loaded.IsOk)
        {
            return Result.Fail<MaterialiseReport>(loaded.Code, loaded.Message, loaded.Hint);
        }

        // Index every existing person by their strong identifiers, so lookup is O(1) per
        // record instead of a scan. With 552 records against a growing store this matters.
        var index = BuildIndex(loaded.ValueOrThrow());

        var byId = loaded.ValueOrThrow().ToDictionary(p => p.Id, StringComparer.Ordinal);

        // Distinct id sets, not event counters. A single person is frequently matched by
        // several records in one run - the same contact appears in both "All Contacts" and
        // "My Contacts" - so counting events double-counts and can drive a derived figure
        // negative. Only set membership is meaningful here.
        var createdIds = new HashSet<string>(StringComparer.Ordinal);
        var touchedIds = new HashSet<string>(StringComparer.Ordinal);
        var mergedRecords = 0;
        var skipped = 0;
        var profilelessSkipped = 0;
        var deletedSkipped = 0;

        foreach (var record in list)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (record is not ObservedAccount account)
            {
                continue;
            }

            var keys = StrongKeys(account);

            if (keys.Count == 0)
            {
                // No phone, no email, no URL: nothing reliable to match on, so this record
                // cannot be merged with anything and would create a fresh person on every
                // run. Skipped and counted rather than ingested, because repeatedly
                // duplicating the same nameless record is worse than omitting it.
                skipped++;
                continue;
            }

            progress?.Invoke(account.DisplayName ?? account.Handle);

            var existingId = keys
                .Select(k => index.GetValueOrDefault(k))
                .FirstOrDefault(id => id is not null);

            // A record whose identifier was previously absorbed into another person must
            // not resurrect the duplicate. Without this check, every ingest run recreates
            // the people the user deliberately merged away.
            if (existingId is not null)
            {
                var absorbing = _mergeLog.FindAbsorbing(existingId);
                if (absorbing is not null)
                {
                    existingId = absorbing.SurvivorId;
                }
            }

            if (existingId is not null && byId.TryGetValue(existingId, out var existing))
            {
                byId[existingId] = Enrich(existing, account, record.Platform);
                touchedIds.Add(existingId);
                mergedRecords++;

                foreach (var key in keys)
                {
                    index[key] = existingId;
                }

                continue;
            }

            // A record that describes no profile may only enrich someone who is already here.
            // A Facebook friend list is 353 bare names, none of which carry a handle or a URL;
            // creating a person from each one gave the workspace 308 records with no source at
            // all - a directory, a name, and nothing behind it. The names are still useful for
            // dating a friendship or filling in a contact that arrived from a phone book, so
            // skip rather than discard outright.
            if (!account.HasProfile)
            {
                profilelessSkipped++;
                continue;
            }

            var id = _store.AllocateId(account.DisplayName ?? account.Handle);

            // AllocateId reuses an existing id when the derived slug collides and the names
            // match, because that is what makes re-ingest idempotent. Here, though, no strong
            // key matched - so the person already holding this id is NOT this record, it just
            // shares a name. Writing Build(...) over it replaced the whole document with the
            // sliver of data one platform supplied, silently deleting phone numbers, emails
            // and every other account.
            //
            // Nine people lost contact data this way when a LinkedIn export arrived carrying a
            // name-derived handle. Enrich rather than overwrite: the record survives, and the
            // new platform is unioned in.
            // A contact the user deleted stays deleted. Every export still contains them, so
            // without this the next ingest recreates the record and the user deletes the same
            // person again after every import - which is worse than having no delete at all.
            if (_deletionLog.Contains(id))
            {
                deletedSkipped++;
                continue;
            }

            if (byId.TryGetValue(id, out var claimed))
            {
                byId[id] = Enrich(claimed, account, record.Platform);
                touchedIds.Add(id);
                mergedRecords++;
            }
            else
            {
                byId[id] = Build(account, record.Platform, id);
                createdIds.Add(id);
                touchedIds.Add(id);
            }

            foreach (var key in keys)
            {
                index[key] = id;
            }
        }

        // Persist only what changed, so an ingest of unchanged data writes nothing and the
        // user's git status stays clean.
        foreach (var id in touchedIds)
        {
            var save = _store.Save(byId[id]);
            if (!save.IsOk)
            {
                return Result.Fail<MaterialiseReport>(save.Code, save.Message, save.Hint);
            }
        }

        var candidates = FindCandidates(byId.Values);

        await Task.CompletedTask.ConfigureAwait(false);

        return Result.Ok(new MaterialiseReport
        {
            RecordsSeen = list.Count,

            // Distinct people, so the arithmetic is internally consistent:
            // created + updated == the number of person files actually written.
            PeopleCreated = createdIds.Count,
            PeopleUpdated = touchedIds.Count - createdIds.Count,
            RecordsMerged = mergedRecords,
            RecordsSkipped = skipped,
            ProfilelessSkipped = profilelessSkipped,
            DeletedSkipped = deletedSkipped,
            Candidates = candidates,
        });
    }

    /// <summary>
    /// Builds the identifier index over existing people.
    /// </summary>
    private static Dictionary<string, string> BuildIndex(IReadOnlyList<Person> people)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in people)
        {
            foreach (var key in StrongKeys(person))
            {
                // First writer wins. If two people already share a phone number the store is
                // inconsistent, and arbitrarily reassigning would make it worse; the
                // duplicate surfaces as a merge candidate instead.
                index.TryAdd(key, person.Id);
            }
        }

        return index;
    }

    /// <summary>
    /// Extracts the identifiers strong enough to merge on.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes names. Phone and email are globally unique in practice; a
    /// profile URL is unique by construction. A name is none of those.
    /// </remarks>
    private static List<string> StrongKeys(ObservedAccount account)
    {
        var keys = new List<string>();

        foreach (var phone in account.Phones)
        {
            var digits = Person.DigitsOnly(phone);
            if (digits.Length >= 7)
            {
                keys.Add($"tel:{digits}");
            }
        }

        foreach (var email in account.Emails)
        {
            if (!string.IsNullOrWhiteSpace(email))
            {
                keys.Add($"email:{email.Trim().ToLowerInvariant()}");
            }
        }

        if (!string.IsNullOrWhiteSpace(account.ProfileUrl))
        {
            keys.Add($"url:{account.ProfileUrl.Trim().TrimEnd('/').ToLowerInvariant()}");
        }

        // A platform handle is a strong identifier in its own right, scoped by platform.
        //
        // Without this, any account arriving with no phone, email or URL was discarded as
        // "no usable identifier" - which is exactly Discord's situation, since its export
        // carries a username and nothing else, and every Instagram follower likewise has no
        // profile URL in the followers file. 122 Discord friends were all dropped.
        //
        // Scoped by platform ("handle:instagram:ada") so the same handle on two platforms
        // does not merge two different people.
        if (!string.IsNullOrWhiteSpace(account.Handle))
        {
            keys.Add($"handle:{account.Platform.ToLowerInvariant()}:{PlatformAccount.NormaliseHandle(account.Handle)}");
        }

        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Extracts strong keys from an existing person.</summary>
    private static List<string> StrongKeys(Person person)
    {
        var keys = new List<string>();

        foreach (var phone in person.Phones)
        {
            var digits = Person.DigitsOnly(phone);
            if (digits.Length >= 7)
            {
                keys.Add($"tel:{digits}");
            }
        }

        foreach (var email in person.Emails)
        {
            keys.Add($"email:{email.Trim().ToLowerInvariant()}");
        }

        foreach (var account in person.Accounts)
        {
            if (!string.IsNullOrWhiteSpace(account.ProfileUrl))
            {
                keys.Add($"url:{account.ProfileUrl.Trim().TrimEnd('/').ToLowerInvariant()}");
            }

            // Mirrors the account-side rule, or an incoming record could never find the
            // person it should merge into once the handle-only key exists on one side.
            if (!string.IsNullOrWhiteSpace(account.Handle))
            {
                keys.Add($"handle:{account.Platform.ToLowerInvariant()}:{PlatformAccount.NormaliseHandle(account.Handle)}");
            }
        }

        return keys;
    }

    /// <summary>Creates a new person from a record.</summary>
    private static Person Build(ObservedAccount account, string platform, string id)
    {
        var name = account.DisplayName ?? account.Handle;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return new Person
        {
            Id = id,
            Name = name,
            Aliases = account.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Phones = account.Phones.Distinct(StringComparer.Ordinal).ToList(),
            Emails = account.Emails.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Tags = account.Groups.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Accounts =
            [
                PlatformAccount.Create(
                    platform,
                    account.Handle,
                    account.DisplayName,
                    account.ProfileUrl,
                    today),
            ],
            Sources = account.AddsSource ? [platform] : [],
            CreatedAt = today,
            UpdatedAt = today,
        };
    }

    /// <summary>
    /// Merges a new record into an existing person without losing hand-edited data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Union, never replace.</b> Ingest must not clobber an alias, location or note the
    /// user typed by hand (DESIGN.md §4.2). Only machine-owned fields are written, and the
    /// display name is only taken from the record when the person has none.
    /// </para>
    /// <para>
    /// <b>Known limitation:</b> union semantics mean a correction to the parser does not
    /// propagate to records already written. When the vCard parser stopped treating
    /// "451-90" as a phone number, existing people kept it, because enriching only adds.
    /// That is the right default — the alternative is re-ingest silently deleting data the
    /// user may have edited — but it means parser fixes need either a fresh workspace or an
    /// explicit repair pass. Making re-ingest destructive to unmanaged fields would be a
    /// separate, opt-in operation rather than the default.
    /// </para>
    /// </remarks>
    private static Person Enrich(Person person, ObservedAccount account, string platform)
    {
        var phones = person.Phones.ToList();

        foreach (var phone in account.Phones)
        {
            if (!phones.Any(p => Person.DigitsOnly(p) == Person.DigitsOnly(phone)))
            {
                phones.Add(phone);
            }
        }

        var emails = person.Emails.ToList();

        foreach (var email in account.Emails)
        {
            if (!emails.Contains(email, StringComparer.OrdinalIgnoreCase))
            {
                emails.Add(email);
            }
        }

        var accounts = person.Accounts.ToList();
        var handle = PlatformAccount.NormaliseHandle(account.Handle);

        if (!accounts.Any(a =>
            string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase)
            && a.HandleNormalised == handle))
        {
            accounts.Add(PlatformAccount.Create(
                platform,
                account.Handle,
                account.DisplayName,
                account.ProfileUrl,
                DateOnly.FromDateTime(DateTime.UtcNow)));
        }

        // A record that describes no profile must not add its platform to the source list:
        // seeing someone's name in a friend list is not evidence they have an account you
        // have, and it produced a "Facebook" badge on people whose only Facebook trace was a
        // name in an export. Such a record still enriches everything else.
        var sources = !account.AddsSource
            || person.Sources.Contains(platform, StringComparer.OrdinalIgnoreCase)
                ? person.Sources
                : [.. person.Sources, platform];

        return person with
        {
            // Only adopt the record's name if the person has none. A name the user edited
            // outranks whatever the platform calls them.
            Name = string.IsNullOrWhiteSpace(person.Name) ? account.DisplayName ?? account.Handle : person.Name,
            Phones = phones,
            Emails = emails,
            Accounts = accounts,
            Sources = sources,
            UpdatedAt = DateOnly.FromDateTime(DateTime.UtcNow),
        };
    }

    /// <summary>
    /// Finds people who look like the same human but were not merged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only names are compared here, and only to <em>suggest</em>. Facebook supplies 353
    /// people with nothing but a name, so without this they would never be linked to their
    /// phone-bearing vCard entry — but merging them automatically would be guessing.
    /// </para>
    /// <para>
    /// Exact matches only. Fuzzy matching produces far more false positives than it resolves,
    /// and a merge candidate the user has to reject is worse than one they never saw.
    /// </para>
    /// </remarks>
    private static List<MergeCandidate> FindCandidates(IEnumerable<Person> people)
    {
        var candidates = new List<MergeCandidate>();
        var byName = new Dictionary<string, List<Person>>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in people)
        {
            var key = NormaliseForComparison(person.Name);

            if (key.Length == 0)
            {
                continue;
            }

            if (!byName.TryGetValue(key, out var list))
            {
                list = [];
                byName[key] = list;
            }

            list.Add(person);
        }

        foreach (var (name, group) in byName)
        {
            if (group.Count < 2)
            {
                continue;
            }

            // Only suggest when the group is not already one person.
            for (var i = 0; i < group.Count; i++)
            {
                for (var j = i + 1; j < group.Count; j++)
                {
                    var left = group[i];
                    var right = group[j];

                    if (ShareStrongIdentifier(left, right))
                    {
                        continue;
                    }

                    candidates.Add(new MergeCandidate(
                        left.Id,
                        right.Id,
                        $"same name \"{left.Name}\" but no shared phone, email or profile URL",
                        0.5));
                }
            }
        }

        return candidates;
    }

    /// <summary>Whether two people already share a strong identifier.</summary>
    private static bool ShareStrongIdentifier(Person left, Person right)
    {
        var leftKeys = StrongKeys(left).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return StrongKeys(right).Any(leftKeys.Contains);
    }

    /// <summary>
    /// Normalises a name for comparison: case-folded, whitespace collapsed, punctuation
    /// stripped.
    /// </summary>
    /// <remarks>
    /// Not used for automatic merging — only to group candidates. "Anne-Marie Smith" and
    /// "Anne Marie Smith" should be offered as a candidate; "Alex Chen" appearing twice may
    /// genuinely be two people, which is exactly why the user decides.
    /// </remarks>
    private static string NormaliseForComparison(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(name.Length);
        var lastWasSpace = false;

        foreach (var character in name.Normalize(System.Text.NormalizationForm.FormD))
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character);

            if (category is System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasSpace = false;
            }
            else if (!lastWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }
}
