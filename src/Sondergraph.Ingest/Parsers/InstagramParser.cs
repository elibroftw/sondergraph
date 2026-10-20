using System.Text.Json;
using Sondergraph.Core;

namespace Sondergraph.Ingest.Parsers;

/// <summary>
/// Parses an Instagram data export.
/// </summary>
/// <remarks>
/// <para>
/// The messy one, and the reason per-platform parsers exist (ADR 0003). Three different
/// record shapes describe the same concept — a followed account
/// (<c>docs/platforms/instagram.md</c>):
/// </para>
/// <list type="table">
/// <item><term>3a</term><description>object keyed <c>relationships_following</c>; handle in <c>title</c>; no <c>value</c></description></item>
/// <item><term>3b</term><description>bare array; <c>title</c> empty; handle in <c>string_list_data[].value</c></description></item>
/// <item><term>3c</term><description>bare array; handle in <c>label_values</c> where label is "Username"</description></item>
/// </list>
/// <para>
/// Two files are <b>excluded</b>: <c>blocked_profiles.json</c> (566 records) and
/// <c>removed_suggestions.json</c> (149). Together 36% of the export, and none of it is a
/// connection — they are people the user blocked or dismissed.
/// </para>
/// </remarks>
public sealed class InstagramParser : IPlatformParser
{
    /// <inheritdoc />
    public string Platform => "instagram";

    /// <inheritdoc />
    public string DisplayName => "Instagram connections";

    /// <inheritdoc />
    public int ParserVersion => 1;

    /// <summary>
    /// Prefix Instagram uses for deactivated or erased accounts.
    /// </summary>
    /// <remarks>
    /// Observed in a real export as 8 records out of ~1,976. Unexported in any documentation;
    /// found by looking at the rendered contact list.
    /// </remarks>
    private const string DeletedPlaceholder = "__deleted__";

    /// <summary>Files to read, mapped to what the relationship means.</summary>
    private static readonly Dictionary<string, InteractionKind> RelationFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // Both directions are recorded, because "follows me" and "I follow them" are
        // different facts. Which of them become contacts is decided by
        // MutualFollowOnly below, not by what is parsed.
        ["following.json"] = InteractionKind.Followed,
        ["followers_1.json"] = InteractionKind.Followed,
        ["close_friends.json"] = InteractionKind.CloseFriend,
        ["pending_follow_requests.json"] = InteractionKind.FriendRequestSent,
        ["recent_follow_requests.json"] = InteractionKind.FriendRequestReceived,
    };

    /// <summary>
    /// Files that are deliberately not ingested.
    /// </summary>
    /// <remarks>
    /// Named explicitly rather than merely omitted, so that the exclusion is a documented
    /// decision and a future reader does not "fix" the gap by adding them.
    /// </remarks>
    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "blocked_profiles.json",          // 566 people the user blocked
        "removed_suggestions.json",       // 149 dismissed strangers
        "restricted_profiles.json",       // schema-valid but content-empty
        "hide_story_from.json",           // a display preference, not a relationship
        "recently_unfollowed_profiles.json",
        "see_less_topics.json",
    };

    /// <inheritdoc />
    public bool CanParse(IRawSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (var file in source.EnumerateFiles())
        {
            var name = Path.GetFileName(file);

            if (RelationFiles.ContainsKey(name) && file.Contains("followers_and_following", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether ingest keeps only people the user follows and who follow back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A workspace preference rather than a parser rule, but the two follow-relation files
    /// are the only place the information exists, so the filter lives beside them.
    /// </para>
    /// <para>
    /// In this export the asymmetry is large: 666 accounts followed, 526 followers, and only
    /// 510 mutual. Keeping the other 166 means a contact list padded with people who will
    /// never be contacted.
    /// </para>
    /// </remarks>
    private const string MutualFollowOnlyKey = "mutualFollowOnly";

    /// <summary>Handles in <c>following.json</c> (accounts the user follows).</summary>
    private static HashSet<string> ReadFollowing(IRawSource source, ParseContext context) =>
        ReadHandles(source, "following.json", context);

    /// <summary>Handles in <c>followers_1.json</c> (accounts that follow the user).</summary>
    private static HashSet<string> ReadFollowers(IRawSource source, ParseContext context) =>
        ReadHandles(source, "followers_1.json", context);

    /// <summary>
    /// Reads the handles from one relation file, tolerating both observed shapes.
    /// </summary>
    /// <remarks>
    /// <c>followers_1.json</c> is a bare array whose entries carry the handle in
    /// <c>string_list_data[].value</c>. <c>following.json</c> is an object keyed
    /// <c>relationships_following</c> whose entries carry it in <c>title</c> and omit
    /// <c>value</c> entirely. Reading only one shape silently yields zero handles for the
    /// other, which would make every account look unmatched.
    /// </remarks>
    private static HashSet<string> ReadHandles(IRawSource source, string fileName, ParseContext context)
    {
        var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var path = source.EnumerateFiles()
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));

        if (path is null)
        {
            return handles;
        }

        var text = source.ReadAllText(path);

        if (string.IsNullOrWhiteSpace(text))
        {
            return handles;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            IEnumerable<JsonElement> records;

            if (root.ValueKind == JsonValueKind.Object)
            {
                records = root.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Array)
                    .SelectMany(p => p.Value.EnumerateArray());
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                records = root.EnumerateArray();
            }
            else
            {
                return handles;
            }

            foreach (var element in records)
            {
                if (ExtractIdentity(element) is { } extracted)
                {
                    handles.Add(PlatformAccount.NormaliseHandle(extracted.Handle));
                }
            }
        }
        catch (JsonException ex)
        {
            context.Report(fileName, "invalid JSON", ex.Message);
        }

        return handles;
    }

    /// <inheritdoc />
    public IEnumerable<ParsedRecord> Parse(IRawSource source, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        var files = source.EnumerateFiles()
            .Where(f => RelationFiles.ContainsKey(Path.GetFileName(f)))
            .Order(StringComparer.Ordinal)
            .ToList();

        // A follow is only kept when it is mutual. The two relation files describe opposite
        // directions, so a single record cannot decide this - it takes both sets.
        var following = ReadFollowing(source, context);
        var followers = ReadFollowers(source, context);

        // Default OFF in the parser, ON at the CLI and API. A parser is a pure function of
        // its input (ADR 0003), so silently discarding records by default would make it lie
        // about the archive: "--dry-run" and the ingest report would no longer describe what
        // the files contain. The policy belongs to the acquisition layer, which asks for it.
        var mutual = context.GetFlag(MutualFollowOnlyKey, defaultValue: false)
            ? new HashSet<string>(following.Intersect(followers, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase)
            : null;

        var dropped = 0;

        foreach (var file in files)
        {
            var kind = RelationFiles[Path.GetFileName(file)];

            foreach (var record in ParseFile(source, file, kind, context))
            {
                // EVERY file reports who the account is, so an account that is not a mutual
                // follow must not become a contact merely because it also appears in a
                // follow-request or close-friends file.
                //
                // The original version filtered only following/followers and let the other
                // three through. A single account commonly appears in several files - one
                // handle in this export is in followers_1, pending_follow_requests and
                // recent_follow_requests at once - so a non-mutual follower leaked in through
                // the side door.
                {
                    if (mutual is not null
                        && record is ObservedAccount account
                        && !mutual.Contains(PlatformAccount.NormaliseHandle(account.Handle)))
                    {
                        dropped++;
                        continue;
                    }
                }

                yield return record;
            }
        }

        if (dropped > 0)
        {
            context.Report(
                "followers_and_following",
                $"skipped {dropped} non-mutual follow(s)");
        }

        // Report the exclusions so they are visible in the ingest summary rather than
        // silently missing.
        foreach (var file in source.EnumerateFiles())
        {
            var name = Path.GetFileName(file);

            if (ExcludedFiles.Contains(name))
            {
                context.Report(file, "excluded by design: not a connection");
            }
        }
    }

    /// <summary>Parses one file, dispatching on its observed shape.</summary>
    private IEnumerable<ParsedRecord> ParseFile(
        IRawSource source,
        string file,
        InteractionKind kind,
        ParseContext context)
    {
        var text = source.ReadAllText(file);

        if (string.IsNullOrWhiteSpace(text))
        {
            context.Report(file, "empty file");
            yield break;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            context.Report(file, "invalid JSON", ex.Message);
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;

            IEnumerable<JsonElement> records;

            if (root.ValueKind == JsonValueKind.Object)
            {
                // Shape 3a: nested under a key.
                records = root.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Array)
                    .SelectMany(p => p.Value.EnumerateArray());
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                // Shapes 3b and 3c: a bare array.
                records = root.EnumerateArray();
            }
            else
            {
                context.Report(file, "unexpected top-level JSON type");
                yield break;
            }

            foreach (var element in records)
            {
                var extracted = ExtractIdentity(element);

                if (extracted is null)
                {
                    context.Report(file, "record has no usable handle");
                    continue;
                }

                var (handle, displayName, profileUrl, timestamp) = extracted.Value;

                var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["idStrategy"] = "handle",
                    ["relation"] = kind.ToString(),
                };

                if (timestamp is not null)
                {
                    extra["timestampRaw"] = timestamp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    // Instagram's timestamps are at or after export time and do not describe
                    // when a relationship started. Recording the uncertainty is the honest
                    // move; presenting them as facts would corrupt the decay feature.
                    extra["timestampSemantics"] = "unreliable";
                }

                yield return new ObservedAccount
                {
                    Platform = Platform,
                    NativeId = $"handle:{PlatformAccount.NormaliseHandle(handle)}",
                    SourcePath = file,
                    Handle = handle,
                    DisplayName = displayName ?? handle,
                    ProfileUrl = profileUrl,
                    Extra = extra,
                };

                if (timestamp is not null
                    && FacebookParser.FromUnixSeconds(timestamp.Value) is { } occurred)
                {
                    yield return new ObservedInteraction
                    {
                        Platform = Platform,
                        NativeId = $"handle:{PlatformAccount.NormaliseHandle(handle)}#{kind}",
                        SourcePath = file,
                        WithHandle = handle,
                        Kind = kind,
                        OccurredAt = occurred,
                    };
                }
            }
        }
    }

    /// <summary>
    /// Extracts a handle from any of the three shapes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback order matters and is not arbitrary. <c>value</c> is preferred because it
    /// is the clean handle; <c>title</c> is next because it holds the handle in one shape and
    /// is <em>empty</em> in another; <c>href</c> is last because it must be parsed and appears
    /// in two different URL forms (<c>/_u/name</c> and <c>/name</c>).
    /// </para>
    /// <para>
    /// Reading <c>title</c> first would produce blank handles for every follower, because
    /// that is exactly the shape where it is empty.
    /// </para>
    /// </remarks>
    internal static (string Handle, string? DisplayName, string? ProfileUrl, long? Timestamp)? ExtractIdentity(
        JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? handle = null;
        string? displayName = null;
        string? profileUrl = null;
        long? timestamp = null;

        // string_list_data: present in shapes 3a and 3b.
        if (element.TryGetProperty("string_list_data", out var listData)
            && listData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in listData.EnumerateArray())
            {
                handle ??= GetString(entry, "value");

                if (profileUrl is null && GetString(entry, "href") is { } href)
                {
                    profileUrl = NormaliseProfileUrl(href);

                    // Two href forms exist: /_u/name and /name. Parse rather than assume.
                    handle ??= HandleFromHref(href);
                }

                timestamp ??= GetLong(entry, "timestamp");
            }
        }

        // title: the handle in shape 3a, empty in 3b.
        if (string.IsNullOrWhiteSpace(handle) && GetString(element, "title") is { } title
            && !string.IsNullOrWhiteSpace(title))
        {
            handle = title.Trim();
        }

        // label_values: shape 3c. Note the label is "Username", and the value key is "value"
        // here - unlike Facebook's suggestion files, which use "vec".
        if (element.TryGetProperty("label_values", out var labels)
            && labels.ValueKind == JsonValueKind.Array)
        {
            foreach (var label in labels.EnumerateArray())
            {
                var key = GetString(label, "label")?.Trim().ToLowerInvariant();

                switch (key)
                {
                    case "username" when string.IsNullOrWhiteSpace(handle):
                        handle = GetString(label, "value");
                        break;

                    case "name":
                        displayName = GetString(label, "value");
                        break;

                    case "url" when profileUrl is null:
                        profileUrl = NormaliseProfileUrl(GetString(label, "value"));
                        break;
                }
            }
        }

        timestamp ??= GetLong(element, "timestamp");

        if (string.IsNullOrWhiteSpace(handle))
        {
            return null;
        }

        // Instagram writes "__deleted__<hash>" for deactivated or erased accounts. These are
        // tombstones, not identities: the user cannot know or contact them, and importing
        // them puts entries like "__deleted__bhiebeabicjfigbee" into the contact list, which
        // is visibly wrong in the UI and inflates every count.
        if (handle.TrimStart().StartsWith(DeletedPlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return (handle.Trim(), displayName, profileUrl, timestamp);
    }

    /// <summary>Extracts a handle from an Instagram profile URL.</summary>
    internal static string? HandleFromHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var trimmed = href.Trim().TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');

        if (lastSlash < 0 || lastSlash == trimmed.Length - 1)
        {
            return null;
        }

        var candidate = trimmed[(lastSlash + 1)..];

        // The "_u" form is /_u/<handle>, so the last segment is already the handle.
        return string.IsNullOrWhiteSpace(candidate) || candidate == "_u"
            ? null
            : candidate;
    }

    private static string? NormaliseProfileUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url.Trim();

    /// <summary>Converts an Instagram href into a canonical profile URL.</summary>
    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;
}
