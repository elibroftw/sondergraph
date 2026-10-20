using System.Globalization;
using System.Text.Json;
using Sondergraph.Core;

namespace Sondergraph.Ingest.Parsers;

/// <summary>
/// Parses a Facebook data export.
/// </summary>
/// <remarks>
/// <para>
/// Two very different kinds of file, and the smaller one is more valuable
/// (<c>docs/platforms/facebook.md</c>):
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Friend lists</b> — uniform <c>{name, timestamp}</c> records with <em>no identifier at
/// all</em>. 353 friends, dated 2014-2026.
/// </item>
/// <item>
/// <b><c>your_imported_contacts.json</c></b> — 179 contacts <em>with phone numbers</em>, the
/// only reachable contact data in the export. Found under
/// <c>personal_information/other_personal_information/</c>, nowhere near <c>connections/</c>.
/// </item>
/// </list>
/// <para>
/// Suggestion files (<c>suggested_friends</c>, <c>people_you_may_know</c>,
/// <c>your_post_audiences</c>) are <b>deliberately excluded</b>: they describe strangers, and
/// including them would inflate the contact graph with people the user has no relationship
/// with.
/// </para>
/// </remarks>
public sealed class FacebookParser : IPlatformParser
{
    /// <inheritdoc />
    public string Platform => "facebook";

    /// <inheritdoc />
    public string DisplayName => "Facebook connections";

    /// <inheritdoc />
    public int ParserVersion => 1;

    /// <summary>
    /// Connection files to read, mapped to the interaction kind they represent.
    /// </summary>
    /// <remarks>
    /// An allowlist, not a glob. Globbing <c>connections/friends/*.json</c> would sweep in
    /// 106 suggested friends and 4 post audiences — strangers, not contacts.
    /// </remarks>
    private static readonly Dictionary<string, InteractionKind> FriendFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["your_friends.json"] = InteractionKind.Friend,
        ["sent_friend_requests.json"] = InteractionKind.FriendRequestSent,
        ["received_friend_requests.json"] = InteractionKind.FriendRequestReceived,
        ["rejected_friend_requests.json"] = InteractionKind.FriendRequestRejected,
        ["removed_friends.json"] = InteractionKind.Unfriended,
        ["who_you've_followed.json"] = InteractionKind.Followed,
    };

    /// <summary>The imported-contacts file, matched by name anywhere in the archive.</summary>
    private const string ImportedContactsFile = "your_imported_contacts.json";

    /// <inheritdoc />
    public bool CanParse(IRawSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (var file in source.EnumerateFiles())
        {
            var name = Path.GetFileName(file);

            if (FriendFiles.ContainsKey(name)
                || name.Equals(ImportedContactsFile, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IEnumerable<ParsedRecord> Parse(IRawSource source, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        var files = source.EnumerateFiles().ToList();

        // Imported contacts first: 179 reachable people beat 353 names, and emitting the
        // valuable data first means a failure later still leaves something useful.
        foreach (var file in files.Where(f =>
            Path.GetFileName(f).Equals(ImportedContactsFile, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var record in ParseImportedContacts(source, file, context))
            {
                yield return record;
            }
        }

        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            if (!FriendFiles.TryGetValue(Path.GetFileName(file), out var kind))
            {
                continue;
            }

            foreach (var record in ParseFriendList(source, file, kind, context))
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// Parses a friend list: a wrapped object whose value is a list of <c>{name, timestamp}</c>.
    /// </summary>
    /// <remarks>
    /// The wrapper key varies with no consistent convention (<c>friends_v2</c>,
    /// <c>deleted_friends_v2</c>, <c>following_v3</c>), so the filename decides the meaning and
    /// the key is only validated.
    /// </remarks>
    private IEnumerable<ParsedRecord> ParseFriendList(
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
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                context.Report(file, "expected an object at the top level");
                yield break;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var element in property.Value.EnumerateArray())
                {
                    var name = GetString(element, "name");

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var timestamp = GetLong(element, "timestamp");
                    var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        // Facebook provides no id whatsoever, so the name is all there is.
                        // Recording the strategy makes it visible that this identity is a
                        // guess, which is why merge candidates exist.
                        ["idStrategy"] = "name",
                        ["wrapperKey"] = property.Name,
                        ["relation"] = kind.ToString(),
                    };

                    if (timestamp is not null)
                    {
                        extra["timestampRaw"] = timestamp.Value.ToString(CultureInfo.InvariantCulture);
                    }

                    yield return new ObservedAccount
                    {
                        Platform = Platform,
                        NativeId = $"name:{Person.DeriveId(name)}",
                        SourcePath = file,
                        Handle = name,
                        DisplayName = name,
                        Extra = extra,
                    };

                    if (timestamp is not null && FromUnixSeconds(timestamp.Value) is { } occurred)
                    {
                        yield return new ObservedInteraction
                        {
                            Platform = Platform,
                            NativeId = $"name:{Person.DeriveId(name)}#{kind}",
                            SourcePath = file,
                            WithHandle = name,
                            Kind = kind,
                            OccurredAt = occurred,
                        };
                    }
                }
            }
        }
    }

    /// <summary>
    /// Parses <c>your_imported_contacts.json</c>: a bare array using <c>label_values</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A third distinct <c>label_values</c> shape, different from both Instagram's and
    /// Facebook's own suggestion files:
    /// </para>
    /// <code>
    /// [
    ///   { "timestamp": 1727408809,
    ///     "label_values": [
    ///       { "label": "Update time",   "timestamp_value": 1778407262 },
    ///       { "label": "Contact name",  "value": "Example Name" },
    ///       { "label": "Contact point", "value": "+15550100123" } ] } ]
    /// </code>
    /// <para>
    /// Note the two extra forms: entries carrying <c>timestamp_value</c> instead of
    /// <c>value</c>, and the suggestion files' <c>vec</c>. A helper shared across these would
    /// silently read nothing from at least one of them.
    /// </para>
    /// </remarks>
    private IEnumerable<ParsedRecord> ParseImportedContacts(
        IRawSource source,
        string file,
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
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                // Unlike the friend lists, this one is a bare array.
                context.Report(file, "expected an array at the top level");
                yield break;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                string? name = null;
                string? contactPoint = null;
                long? updatedAt = null;

                if (element.TryGetProperty("label_values", out var labels)
                    && labels.ValueKind == JsonValueKind.Array)
                {
                    foreach (var label in labels.EnumerateArray())
                    {
                        var key = GetString(label, "label");

                        switch (key?.Trim().ToLowerInvariant())
                        {
                            case "contact name":
                                name = GetString(label, "value");
                                break;

                            case "contact point":
                                contactPoint = GetString(label, "value");
                                break;

                            case "update time":
                                updatedAt = GetLong(label, "timestamp_value");
                                break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(contactPoint))
                {
                    context.Report(file, "contact record has neither a name nor a contact point");
                    continue;
                }

                var display = string.IsNullOrWhiteSpace(name) ? contactPoint! : name!;
                var phones = new List<string>();

                if (!string.IsNullOrWhiteSpace(contactPoint) && LooksLikePhone(contactPoint))
                {
                    phones.Add(contactPoint);
                }

                var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // Keyed on the phone when there is one, matching the vCard strategy so
                    // the same person merges across both sources.
                    ["idStrategy"] = phones.Count > 0 ? "phone" : "name",
                    ["relation"] = "ImportedContact",
                };

                if (updatedAt is not null)
                {
                    extra["updateTimeRaw"] = updatedAt.Value.ToString(CultureInfo.InvariantCulture);
                }

                var nativeId = phones.Count > 0
                    ? $"tel:{Person.DigitsOnly(phones[0])}"
                    : $"name:{Person.DeriveId(display)}";

                yield return new ObservedAccount
                {
                    Platform = Platform,
                    NativeId = nativeId,
                    SourcePath = file,
                    Handle = display,
                    DisplayName = display,
                    Phones = phones,
                    Extra = extra,
                };
            }
        }
    }

    /// <summary>Whether a contact point looks like a phone number rather than an email.</summary>
    private static bool LooksLikePhone(string value)
    {
        if (value.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        return Person.DigitsOnly(value).Length >= 7;
    }

    /// <summary>Converts Unix epoch seconds, rejecting implausible values.</summary>
    /// <remarks>
    /// Bounds-checked because a zero or garbage timestamp must not become 1970 in the
    /// timeline, and a future-dated one must not distort the decay feature.
    /// </remarks>
    internal static DateTimeOffset? FromUnixSeconds(long seconds)
    {
        // 2004-01-01 through roughly 2035: plausible for a social account.
        if (seconds < 1_072_915_200 || seconds > 2_051_222_400)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

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
