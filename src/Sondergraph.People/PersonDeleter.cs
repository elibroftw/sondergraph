using System.Text.Json;
using Sondergraph.Core;

namespace Sondergraph.People;

/// <summary>What a deletion changed, so the caller can report it.</summary>
/// <param name="Deleted">The person, as they were.</param>
/// <param name="ClearedMutuals">How many other contacts had them as a mutual.</param>
/// <param name="ClearedIntroducers">How many other contacts had them as their introducer.</param>
public sealed record DeletionResult(
    Person Deleted,
    int ClearedMutuals,
    int ClearedIntroducers);

/// <summary>
/// Deletes a contact, without leaving dangling references behind.
/// </summary>
/// <remarks>
/// <para>
/// A record is not self-contained: other people list it as a mutual and may name it as the
/// person who introduced them. Removing only the file would leave those ids pointing at
/// nothing - the graph would lose edges silently, and a panel would render an id where a name
/// should be.
/// </para>
/// <para>
/// The deletion is recorded before the file is removed. If the write of the log fails, nothing
/// is deleted; if removing the directory fails afterwards, the record is gone from the log only
/// by being reloaded, and the person is still present - either way the two never disagree about
/// whether the person exists.
/// </para>
/// </remarks>
public sealed class PersonDeleter
{
    private readonly PersonStore _store;
    private readonly DeletionLog _log;

    public PersonDeleter(PersonStore store, DeletionLog log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _log = log;
    }

    /// <summary>Deletes a contact and cleans up every reference to them.</summary>
    public Result<DeletionResult> Delete(string personId)
    {
        if (string.IsNullOrWhiteSpace(personId))
        {
            return Result.Fail<DeletionResult>(ErrorCode.Unknown, "A person id is required.");
        }

        var loaded = _store.Load(personId);

        if (!loaded.IsOk)
        {
            return Result.Fail<DeletionResult>(loaded.Code, loaded.Message, loaded.Hint);
        }

        var person = loaded.ValueOrThrow();

        // Record the decision first, so a failure to write leaves the workspace untouched
        // rather than deleting a contact that would come back on the next import.
        var logged = _log.Append(new DeletionRecord
        {
            Id = person.Id,
            Name = person.Name,
            At = DateTimeOffset.UtcNow,
            Document = PersonDocument.Serialize(person),
        });

        if (!logged.IsOk)
        {
            return Result.Fail<DeletionResult>(logged.Code, logged.Message, logged.Hint);
        }

        // Clean every reference before the file disappears, so a crash part-way leaves a record
        // that still exists but is no longer referenced, rather than one that does not exist
        // and is.
        var everyone = _store.LoadAllIncludingDeleted();

        if (!everyone.IsOk)
        {
            return Result.Fail<DeletionResult>(everyone.Code, everyone.Message, everyone.Hint);
        }

        var clearedMutuals = 0;
        var clearedIntroducers = 0;

        foreach (var other in everyone.ValueOrThrow())
        {
            if (string.Equals(other.Id, person.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mutuals = other.FriendsWith
                .Where(id => !string.Equals(id, person.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var introducer = string.Equals(other.KnownThrough, person.Id, StringComparison.OrdinalIgnoreCase)
                ? null
                : other.KnownThrough;

            if (mutuals.Count == other.FriendsWith.Count && introducer == other.KnownThrough)
            {
                continue;
            }

            if (mutuals.Count != other.FriendsWith.Count)
            {
                clearedMutuals++;
            }

            if (introducer != other.KnownThrough)
            {
                clearedIntroducers++;
            }

            var saved = _store.Save(other with
            {
                FriendsWith = mutuals,
                KnownThrough = introducer,
                UpdatedAt = DateOnly.FromDateTime(DateTime.UtcNow),
            });

            if (!saved.IsOk)
            {
                return Result.Fail<DeletionResult>(saved.Code, saved.Message, saved.Hint);
            }
        }

        var removed = _store.Remove(person.Id);

        if (!removed.IsOk)
        {
            return Result.Fail<DeletionResult>(removed.Code, removed.Message, removed.Hint);
        }

        return Result.Ok(new DeletionResult(person, clearedMutuals, clearedIntroducers));
    }
}
