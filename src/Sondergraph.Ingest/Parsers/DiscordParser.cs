using System.Text.Json;
using Sondergraph.Core;

namespace Sondergraph.Ingest.Parsers;

/// <summary>
/// Parses a Discord data package into the people it names.
/// </summary>
/// <remarks>
/// <para>
/// Discord's package is mostly account plumbing - billing, applications, virtual currency -
/// with one file that matters here: <c>Account/user.json</c>, whose <c>relationships</c> array
/// is the friends list.
/// </para>
/// <para>
/// <b>Every relationship has a type, and the type decides what it is.</b> A naive reader that
/// takes every entry produces contacts for people the user has <em>blocked</em> and for
/// incoming requests they never accepted. Only <c>FRIEND</c> means "this is someone I know";
/// the rest are recorded as facts, not as contacts.
/// </para>
/// </remarks>
public sealed class DiscordParser : IPlatformParser
{
    /// <inheritdoc />
    public string Platform => "discord";

    /// <inheritdoc />
    public string DisplayName => "Discord friends";

    /// <inheritdoc />
    public int ParserVersion => 1;

    /// <summary>The only file in the package that carries people.</summary>
    private const string UserFile = "Account/user.json";

    /// <summary>
    /// Relationship types that mean the person is an actual contact.
    /// </summary>
    /// <remarks>
    /// <c>BLOCKED</c> is excluded deliberately: a blocked account is emphatically not a
    /// contact, and importing it would be worse than merely useless. Pending requests are
    /// excluded too, because neither side has accepted. They are still reported so the user
    /// can see they were considered rather than lost.
    /// </remarks>
    private static readonly HashSet<string> ContactTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FRIEND",
    };

    /// <inheritdoc />
    public bool CanParse(IRawSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.EnumerateFiles()
            .Any(f => f.Replace('\\', '/').EndsWith(UserFile, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public IEnumerable<ParsedRecord> Parse(IRawSource source, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        var file = source.EnumerateFiles()
            .FirstOrDefault(f => f.Replace('\\', '/').EndsWith(UserFile, StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            yield break;
        }

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
            if (!document.RootElement.TryGetProperty("relationships", out var relationships)
                || relationships.ValueKind != JsonValueKind.Array)
            {
                context.Report(file, "no relationships array");
                yield break;
            }

            var skipped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var relationship in relationships.EnumerateArray())
            {
                var type = GetString(relationship, "type") ?? "NONE";

                if (!ContactTypes.Contains(type))
                {
                    // Counted by type so the report says which kinds were left out, rather
                    // than a single opaque number.
                    skipped[type] = skipped.GetValueOrDefault(type) + 1;
                    continue;
                }

                if (!relationship.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
                {
                    context.Report(file, "friend entry has no user object");
                    continue;
                }

                var id = GetString(user, "id");

                if (string.IsNullOrWhiteSpace(id))
                {
                    context.Report(file, "friend entry has no id");
                    continue;
                }

                var username = GetString(user, "username");
                var globalName = GetString(user, "global_name");

                // A nickname is what the user calls this person, which is far more useful
                // than a Discord handle: "George Lewis" for "_soup_can_2".
                var nickname = GetString(relationship, "nickname");

                var preferred = new[] { nickname, globalName, username }
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? id;

                var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["idStrategy"] = "discord-id",
                    ["relationship"] = type,
                };

                if (!string.IsNullOrWhiteSpace(nickname))
                {
                    extra["nickname"] = nickname;
                }

                // Username and global name are both identifiers: people search for either,
                // whichever one happens to be shown. Filtering out the alias that equals the
                // display name was wrong - when there is no nickname the global name IS the
                // display name, and dropping it left the handle-only remainder.
                var aliases = new[] { username, globalName }
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                yield return new ObservedAccount
                {
                    Platform = Platform,
                    NativeId = $"discord:{id}",
                    SourcePath = file,
                    Handle = username ?? id,
                    DisplayName = preferred,
                    // Discord has no public profile URL for a user id, so none is invented.
                    ProfileUrl = null,
                    Aliases = aliases,
                    Extra = extra,
                };
            }

            if (skipped.Count > 0)
            {
                var summary = string.Join(
                    ", ",
                    skipped.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"));

                context.Report(file, $"left out non-friends: {summary}");
            }
        }
    }

    /// <summary>Reads a string property, treating absent and null alike.</summary>
    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
