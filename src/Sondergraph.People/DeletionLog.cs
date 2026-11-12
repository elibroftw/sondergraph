using System.Text.Json;
using System.Text.Json.Serialization;
using Sondergraph.Core;

namespace Sondergraph.People;

/// <summary>A contact the user deleted.</summary>
/// <remarks>
/// The whole document is kept, not just the id. Deleting is meant to be a decision the user
/// can still inspect afterwards - "I know I removed someone called this" - and a record with
/// only an id answers nothing. It also makes a restore possible without going to a backup.
/// </remarks>
public sealed record DeletionRecord
{
    /// <summary>Person id that was deleted.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Their name, for the review list.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>When the deletion happened.</summary>
    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }

    /// <summary>The document as it was, verbatim.</summary>
    [JsonPropertyName("document")]
    public required string Document { get; init; }
}

/// <summary>
/// Remembers deleted contacts so a re-import cannot bring them back.
/// </summary>
/// <remarks>
/// <para>
/// Without this, deleting is temporary: every export still contains the person, and the next
/// ingest recreates them. The user would delete the same contact after every import, which is
/// worse than not offering delete at all.
/// </para>
/// <para>
/// Append-only and stored beside the merge log, so the two kinds of "this record should not
/// come back" decision live in the same place and are reviewed the same way.
/// </para>
/// </remarks>
public sealed class DeletionLog
{
    private readonly string _path;
    private readonly List<DeletionRecord> _records = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private DeletionLog(string path, List<DeletionRecord> records)
    {
        _path = path;
        _records = records;
    }

    /// <summary>Every deletion, oldest first.</summary>
    public IReadOnlyList<DeletionRecord> Records => _records;

    /// <summary>Whether this id has been deleted.</summary>
    public bool Contains(string personId) =>
        _records.Any(r => string.Equals(r.Id, personId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An in-memory log holding nothing.
    /// </summary>
    /// <remarks>
    /// For callers that do not care about deletions - tests, and code paths that never create
    /// people. Never written to disk, so it cannot accidentally erase a real log.
    /// </remarks>
    public static DeletionLog Empty() => new(string.Empty, []);

    /// <summary>Loads the log, returning an empty one when the file does not exist.</summary>
    public static Result<DeletionLog> Load(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, "data", "deleted.json");

        if (!File.Exists(path))
        {
            return Result.Ok(new DeletionLog(path, []));
        }

        try
        {
            var json = File.ReadAllText(path);

            if (string.IsNullOrWhiteSpace(json))
            {
                return Result.Ok(new DeletionLog(path, []));
            }

            var records = JsonSerializer.Deserialize<List<DeletionRecord>>(json) ?? [];
            return Result.Ok(new DeletionLog(path, records));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result.Fail<DeletionLog>(
                ErrorCode.WorkspaceInvalid,
                $"Could not read the deletion log at '{path}': {ex.Message}",
                "The log is append-only history; restore it from git or delete it to start fresh.");
        }
    }

    /// <summary>Appends a deletion and persists the log.</summary>
    public Result Append(DeletionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _records.Add(record);
        return Save();
    }

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
                $"Could not write the deletion log: {ex.Message}");
        }
    }
}
