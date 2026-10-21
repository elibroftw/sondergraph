using System.Text.Json;

namespace Sondergraph.Ingest.Parsers;

/// <summary>
/// Parses a Twitter/X archive into the accounts followed in both directions.
/// </summary>
/// <remarks>
/// <para>
/// <b>The archive contains no names.</b> <c>data/follower.js</c> and
/// <c>data/following.js</c> hold nothing but numeric account ids and a link of the form
/// <c>https://twitter.com/intent/user?user_id=...</c>; no username, no display name, and no
/// other file in the archive references those ids. Verified across all 6,675 entries in a
/// real 269 MB archive.
/// </para>
/// <para>
/// So every record this parser produces is named after its id. That is a deliberate trade:
/// the ids identify exactly the right people, and the app can rename them, whereas inventing
/// a name would be fabrication. The profile URL is derived from the id so a contact is one
/// click from being identified, which is what makes the rename practical at all.
/// </para>
/// <para>
/// <b>Only mutual follows are kept.</b> A one-directional follow is not a relationship: the
/// requested rule is people followed in both directions, and in this archive that is 101 of
/// 241 followers and 273 following.
/// </para>
/// </remarks>
public sealed class TwitterParser : IPlatformParser
{
    /// <inheritdoc />
    public string Platform => "twitter";

    /// <inheritdoc />
    public string DisplayName => "Twitter mutual follows";

    /// <inheritdoc />
    public int ParserVersion => 1;

    private const string FollowerFile = "data/follower.js";
    private const string FollowingFile = "data/following.js";

    /// <inheritdoc />
    public bool CanParse(IRawSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var files = source.EnumerateFiles()
            .Select(f => f.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return files.Contains(FollowerFile) || files.Contains(FollowingFile);
    }

    /// <inheritdoc />
    public IEnumerable<ParsedRecord> Parse(IRawSource source, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        var followerFile = Find(source, FollowerFile);
        var followingFile = Find(source, FollowingFile);

        var followers = followerFile is null
            ? []
            : ReadIds(source, followerFile, "follower", context);

        var following = followingFile is null
            ? []
            : ReadIds(source, followingFile, "following", context);

        if (followers.Count == 0 || following.Count == 0)
        {
            // Without both directions there is no way to decide who is mutual, and importing
            // one side would silently produce one-directional follows under a label claiming
            // they are mutual.
            context.Report(
                FollowerFile,
                "need both follower.js and following.js to determine mutual follows");

            yield break;
        }

        var mutual = followers.Intersect(following, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        context.Report(
            "mutual",
            $"kept {mutual.Count} mutual of {followers.Count} followers and {following.Count} following");

        foreach (var id in mutual)
        {
            var url = ProfileUrl(id);

            yield return new ObservedAccount
            {
                Platform = Platform,
                NativeId = $"twitter:{id}",
                SourcePath = followingFile!,
                Handle = id,
                // Named after the id because that is all the archive provides. Visible and
                // correctable beats a fabricated name.
                DisplayName = id,
                ProfileUrl = url,
                Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["idStrategy"] = "twitter-id",
                    ["mutualFollow"] = "true",
                    // Signals to whoever reads the record that the name is a placeholder
                    // rather than something a source asserted.
                    ["nameUnavailable"] = "true",
                },
            };
        }
    }

    /// <summary>
    /// The profile URL for an account id.
    /// </summary>
    /// <remarks>
    /// <c>https://x.com/i/user/&lt;id&gt;</c> redirects to the profile for any account that
    /// still exists, which is what makes these records usable before they are renamed.
    /// </remarks>
    private static string ProfileUrl(string id) => $"https://x.com/i/user/{id}";

    private static string? Find(IRawSource source, string name) =>
        source.EnumerateFiles().FirstOrDefault(f =>
            f.Replace('\\', '/').EndsWith(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the account ids from one of the two follow files.</summary>
    /// <remarks>
    /// The files are JavaScript assignments ("window.YTD.follower.part0 = [ ... ]"), not bare
    /// JSON, so the array is located by its brackets rather than parsed as a document. The
    /// leading assignment confuses every JSON parser, which is why this cannot be a
    /// JsonDocument.Parse call.
    /// </remarks>
    private static List<string> ReadIds(
        IRawSource source,
        string file,
        string key,
        ParseContext context)
    {
        var ids = new List<string>();
        var text = source.ReadAllText(file);

        if (string.IsNullOrWhiteSpace(text))
        {
            context.Report(file, "empty file");
            return ids;
        }

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');

        if (start < 0 || end <= start)
        {
            context.Report(file, "no JSON array found");
            return ids;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text[start..(end + 1)]);
        }
        catch (JsonException ex)
        {
            context.Report(file, "invalid JSON", ex.Message);
            return ids;
        }

        using (document)
        {
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!node.TryGetProperty("accountId", out var accountId)
                    || accountId.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = accountId.GetString();

                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id.Trim());
                }
            }
        }

        return ids;
    }
}
