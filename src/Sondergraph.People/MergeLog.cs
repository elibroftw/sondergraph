using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sondergraph.Core;

namespace Sondergraph.People;

/// <summary>
/// A record of one merge, sufficient to reverse it.
/// </summary>
/// <remarks>
/// <para>
/// Merging is the riskiest operation in the system (DESIGN.md §4.3), so it is recorded rather
/// than destructive. The absorbed person's full record is stored here verbatim, which makes
/// <c>ungraft</c> a restore rather than a reconstruction — reconstruction from a diff would
/// be fragile, and this data is small.
/// </para>
/// </remarks>
public sealed record MergeRecord
{
    /// <summary>Identifier for this merge, used by <c>sg ungraft</c>.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>When the merge happened.</summary>
    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }

    /// <summary>The person that survived.</summary>
    [JsonPropertyName("survivorId")]
    public required string SurvivorId { get; init; }

    /// <summary>The person that was absorbed.</summary>
    [JsonPropertyName("absorbedId")]
    public required string AbsorbedId { get; init; }

    /// <summary>The absorbed person's document, verbatim, for restoration.</summary>
    [JsonPropertyName("absorbedDocument")]
    public required string AbsorbedDocument { get; init; }

    /// <summary>The survivor's document before the merge, for restoration.</summary>
    [JsonPropertyName("survivorDocumentBefore")]
    public required string SurvivorDocumentBefore { get; init; }

    /// <summary>Why the merge was suggested, when it came from resolution.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// An append-only log of merges, stored at <c>data/merges.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Append-only on purpose: a reversing entry is added rather than an existing one removed,
/// so the file is a complete history of how the graph got to its current shape. That is what
/// makes "why is this one person and not two?" answerable months later.
/// </para>
/// <para>
/// The whole file is rewritten on each append via <c>AtomicFile</c>. Merges are rare and the
/// file is small, so the simplicity is worth more than incremental writes.
/// </para>
/// </remarks>
public sealed class MergeLog
{
    private readonly string _path;
    private readonly List<MergeRecord> _records = [];

    /// <summary>
    /// Cached serialiser options. Reused rather than constructed per call, both because the
    /// analyzer requires it and because <see cref="JsonSerializerOptions"/> caches type
    /// metadata internally — creating one per write would defeat that.
    /// </summary>
    /// <remarks>
    /// The relaxed encoder matters: without it, non-ASCII names are emitted as <c>\uXXXX</c>
    /// escapes, which makes the log unreadable to the humans who are meant to inspect it.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private MergeLog(string path, List<MergeRecord> records)
    {
        _path = path;
        _records = records;
    }

    /// <summary>All merges, oldest first.</summary>
    public IReadOnlyList<MergeRecord> Records => _records;

    /// <summary>Merges that have not been reversed.</summary>
    public IEnumerable<MergeRecord> Active =>
        _records.Where(r => !_records.Any(u =>
            u.Reason == $"undo:{r.Id}"));

    /// <summary>Loads the log, returning an empty one when the file does not exist.</summary>
    public static Result<MergeLog> Load(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, "data", "merges.json");

        if (!File.Exists(path))
        {
            return Result.Ok(new MergeLog(path, []));
        }

        try
        {
            var json = File.ReadAllText(path);

            if (string.IsNullOrWhiteSpace(json))
            {
                return Result.Ok(new MergeLog(path, []));
            }

            var records = JsonSerializer.Deserialize<List<MergeRecord>>(json) ?? [];
            return Result.Ok(new MergeLog(path, records));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result.Fail<MergeLog>(
                ErrorCode.WorkspaceInvalid,
                $"Could not read the merge log at '{path}': {ex.Message}",
                "The log is append-only history; restore it from git or delete it to start fresh.");
        }
    }

    /// <summary>Appends a merge and persists the log.</summary>
    public Result Append(MergeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _records.Add(record);
        return Save();
    }

    /// <summary>
    /// Finds the merge that absorbed <paramref name="personId"/>, if any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used during resolution to avoid re-creating a person that was deliberately merged
    /// away. Without this, every ingest run would resurrect absorbed duplicates and the user
    /// would re-merge them forever.
    /// </para>
    /// <para>
    /// Follows the whole chain, not one hop. A person merged into another who is themselves
    /// later merged forms a redirect chain: A into B, then B into C. Looking up A must land on
    /// C. Returning B would name a survivor that has itself been absorbed, and the caller
    /// would then load a tombstone or recreate the record it was told to avoid.
    /// </para>
    /// <para>
    /// Real case: a vCard contact with a phone number was merged into the Instagram record,
    /// which was merged into a third. Every later ingest that looked up the vCard record
    /// resolved to the middle of the chain instead of the end, so the number never found its
    /// way back to the surviving person.
    /// </para>
    /// </remarks>
    public MergeRecord? FindAbsorbing(string personId)
    {
        // Bounded by the number of merges: a cycle would otherwise loop forever, and cycles
        // are only possible through a corrupted log.
        var current = personId;
        MergeRecord? last = null;

        for (var step = 0; step < _records.Count + 1; step++)
        {
            var next = _records.LastOrDefault(r =>
                string.Equals(r.AbsorbedId, current, StringComparison.Ordinal)
                && !_records.Any(u => u.Reason == $"undo:{r.Id}"));

            if (next is null)
            {
                return last;
            }

            last = next;
            current = next.SurvivorId;
        }

        return last;
    }

    /// <summary>Finds a merge by its id.</summary>
    public MergeRecord? Find(string mergeId) =>
        _records.FirstOrDefault(r => string.Equals(r.Id, mergeId, StringComparison.Ordinal));

    /// <summary>Records that a merge was reversed.</summary>
    public Result RecordUndo(string mergeId) =>
        Append(new MergeRecord
        {
            Id = Guid.NewGuid().ToString("n")[..8],
            At = DateTimeOffset.UtcNow,
            SurvivorId = string.Empty,
            AbsorbedId = string.Empty,
            AbsorbedDocument = string.Empty,
            SurvivorDocumentBefore = string.Empty,
            Reason = $"undo:{mergeId}",
        });

    private Result Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_records, JsonOptions);

            return Sondergraph.Workspaces.AtomicFile.WriteAllText(_path, json + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"Could not write the merge log: {ex.Message}");
        }
    }

    /// <summary>Renders the log for <c>sg merge-log</c>.</summary>
    public string Describe()
    {
        var output = new StringBuilder();

        foreach (var record in _records)
        {
            if (record.Reason?.StartsWith("undo:", StringComparison.Ordinal) == true)
            {
                output.Append("  undo  ").Append(record.Reason[5..])
                    .Append("  at ").Append(record.At.ToLocalTime().ToString("u"))
                    .Append('\n');
                continue;
            }

            output.Append("  ").Append(record.Id)
                .Append("  ").Append(record.AbsorbedId)
                .Append(" -> ").Append(record.SurvivorId)
                .Append("  at ").Append(record.At.ToLocalTime().ToString("u"));;

            if (!string.IsNullOrWhiteSpace(record.Reason))
            {
                output.Append("  (").Append(record.Reason).Append(')');
            }

            output.Append('\n');
        }

        return output.ToString();
    }
}
