using System.Globalization;
using System.Text;

namespace Sondergraph.People.Markdown;

/// <summary>
/// Small helpers for producing predictable markdown.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from a library, because the output must be stable, diffable
/// and hand-editable (DESIGN.md §4.2). A general YAML emitter optimises for correctness of
/// serialisation; we optimise for a human reading and editing the file, which means fixed
/// field order, no anchors, no flow-style surprises, and quoted strings only when necessary.
/// </remarks>
internal static class Yaml
{
    /// <summary>
    /// Quotes a scalar only when leaving it bare would change its meaning.
    /// </summary>
    /// <remarks>
    /// Over-quoting is the common failure of hand-rolled emitters and makes files unpleasant
    /// to read. The cases that genuinely need quoting are: empty, leading/trailing space,
    /// YAML punctuation at the start, characters that imply another type, and anything
    /// containing a colon-space or a comment marker.
    /// </remarks>
    internal static string Scalar(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var needsQuotes =
            value != value.Trim()
            || "!&*-?|>%@`\"'#,[]{}:".Contains(value[0], StringComparison.Ordinal)
            || value.Contains(": ", StringComparison.Ordinal)
            || value.Contains(" #", StringComparison.Ordinal)
            || value.EndsWith(':')
            || value.Contains('\n')
            || value.Contains('\r')
            || value.Contains('\t')
            // Values that YAML would otherwise read as another type.
            || IsReservedWord(value)
            || LooksNumeric(value)
            || value.Contains("  ", StringComparison.Ordinal);

        if (!needsQuotes)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static bool IsReservedWord(string value) =>
        value is "true" or "false" or "yes" or "no" or "on" or "off"
            or "null" or "~" or "True" or "False" or "Null" or "NULL" or "Yes" or "No";

    private static bool LooksNumeric(string value) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Parses a leading <c>---</c> fenced frontmatter block.
    /// </summary>
    /// <remarks>
    /// Understands the subset this project writes: <c>key: value</c> scalars and
    /// <c>key:</c> followed by <c>  - item</c> lists. Deliberately not a full YAML parser —
    /// a dependency for a known-shape document is not worth it, and an unparseable file is
    /// reported rather than silently ignored.
    /// </remarks>
    internal static (Dictionary<string, string> Scalars, Dictionary<string, List<string>> Lists)
        ParseFrontmatter(string text)
    {
        var scalars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return (scalars, lists);
        }

        string? currentList = null;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.Trim() == "---")
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // A list item belonging to the most recent "key:".
            if (line.StartsWith("  - ", StringComparison.Ordinal) && currentList is not null)
            {
                lists[currentList].Add(Unquote(line[4..].Trim()));
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (value.Length == 0)
            {
                currentList = key;
                lists[key] = [];
            }
            else
            {
                currentList = null;
                scalars[key] = Unquote(value);
            }
        }

        return (scalars, lists);
    }

    /// <summary>Removes surrounding quotes and resolves escapes.</summary>
    internal static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var inner = value[1..^1];
        var builder = new StringBuilder(inner.Length);

        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i + 1 >= inner.Length)
            {
                builder.Append(inner[i]);
                continue;
            }

            i++;
            builder.Append(inner[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => inner[i],
            });
        }

        return builder.ToString();
    }

    /// <summary>Reads a <c>yyyy-MM-dd</c> date, or null.</summary>
    internal static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>Formats a date as <c>yyyy-MM-dd</c>.</summary>
    internal static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Appends a list to the output, writing <c>key: []</c> when empty.
    /// </summary>
    /// <remarks>
    /// An explicit empty list is better than omitting the key: it tells a human reader the
    /// field exists and is intentionally blank, rather than leaving them to wonder whether
    /// the tool forgot to write it.
    /// </remarks>
    internal static void WriteList(StringBuilder output, string key, IEnumerable<string> values)
    {
        var items = values.ToList();

        if (items.Count == 0)
        {
            output.Append(key).Append(": []\n");
            return;
        }

        output.Append(key).Append(":\n");

        foreach (var item in items)
        {
            output.Append("  - ").Append(Scalar(item)).Append('\n');
        }
    }
}
