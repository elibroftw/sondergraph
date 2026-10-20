using System.Globalization;
using Sondergraph.Core;

namespace Sondergraph.Ingest.Parsers;

/// <summary>
/// Parses a LinkedIn data export.
/// </summary>
/// <remarks>
/// <para>
/// The source is CSV, and the connections file is the valuable part: 683 connections, every
/// one dated, each with a stable profile URL. See <c>docs/platforms/linkedin.md</c>.
/// </para>
/// <para>
/// Two traps, both observed in the real export:
/// </para>
/// <list type="number">
/// <item>
/// <b>The file does not start with a header.</b> It begins with a <c>Notes:</c> block. A
/// naive reader treats the first note line as the header and produces garbage for every row.
/// </item>
/// <item>
/// <b><c>Position</c> contains commas and is quoted.</b> Splitting on commas corrupts it.
/// </item>
/// </list>
/// </remarks>
public sealed class LinkedinParser : IPlatformParser
{
    /// <inheritdoc />
    public string Platform => "linkedin";

    /// <inheritdoc />
    public string DisplayName => "LinkedIn connections";

    /// <inheritdoc />
    public int ParserVersion => 1;

    /// <summary>Header line that marks the start of the real CSV data.</summary>
    private const string HeaderMarker = "First Name,";

    /// <summary>Date format used by the <c>Connected On</c> column.</summary>
    /// <remarks>
    /// <c>dd MMM yyyy</c>, e.g. <c>16 Oct 2026</c>. The only non-numeric date format in the
    /// whole dataset, and the only one that needs an explicit culture: parsed under a
    /// non-English locale it either throws or silently mis-reads the month.
    /// </remarks>
    private const string DateFormat = "dd MMM yyyy";

    /// <inheritdoc />
    public bool CanParse(IRawSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (var file in source.EnumerateFiles())
        {
            if (Path.GetFileName(file).Equals("Connections.csv", StringComparison.OrdinalIgnoreCase))
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

        var file = source.EnumerateFiles().FirstOrDefault(f =>
            Path.GetFileName(f).Equals("Connections.csv", StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            yield break;
        }

        var text = source.ReadAllText(file);

        if (string.IsNullOrWhiteSpace(text))
        {
            context.Report(file, "Connections.csv is empty");
            yield break;
        }

        var rows = ReadRows(text, file, context);

        foreach (var row in rows)
        {
            row.TryGetValue("First Name", out var first);
            row.TryGetValue("Last Name", out var last);
            row.TryGetValue("URL", out var url);
            row.TryGetValue("Email Address", out var email);
            row.TryGetValue("Company", out var company);
            row.TryGetValue("Position", out var position);
            row.TryGetValue("Connected On", out var connectedOn);

            var name = string.Join(' ', new[] { first, last }
                .Where(p => !string.IsNullOrWhiteSpace(p))).Trim();

            if (name.Length == 0)
            {
                context.Report(file, "row has no name");
                continue;
            }

            // The URL is the strongest identifier available: stable and globally unique.
            // A name is not, so it is the fallback and the strategy is recorded.
            var (nativeId, strategy) = !string.IsNullOrWhiteSpace(url)
                ? (url.Trim(), "profileUrl")
                : ($"name:{Person.DeriveId(name)}", "name");

            var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["idStrategy"] = strategy,
            };

            if (!string.IsNullOrWhiteSpace(company))
            {
                extra["company"] = company.Trim();
            }

            if (!string.IsNullOrWhiteSpace(position))
            {
                extra["position"] = position.Trim();
            }

            if (!string.IsNullOrWhiteSpace(connectedOn))
            {
                extra["connectedOnRaw"] = connectedOn.Trim();
            }

            DateTimeOffset? connectedAt = TryParseDate(connectedOn);

            var account = new ObservedAccount
            {
                Platform = Platform,
                NativeId = nativeId,
                SourcePath = file,
                Handle = name,
                DisplayName = name,
                ProfileUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim(),
                // Notification addresses are not people. A LinkedIn export can carry rows
                // harvested from a mailbox, and those include reply+...@reply.github.com.
                Emails = string.IsNullOrWhiteSpace(email)
                    ? []
                    : [.. ContactEmails.KeepPersonAddresses([email])],
                Extra = extra,
            };

            yield return account;

            // Emit the connection as an interaction so the timeline and decay features have
            // something to work with. This is the only source with a real date per contact.
            if (connectedAt is not null)
            {
                yield return new ObservedInteraction
                {
                    Platform = Platform,
                    NativeId = $"{nativeId}#connected",
                    SourcePath = file,
                    WithHandle = name,
                    Kind = InteractionKind.Connected,
                    OccurredAt = connectedAt,
                };
            }
        }
    }

    /// <summary>
    /// Parses the CSV, skipping the note block before the header.
    /// </summary>
    /// <remarks>
    /// The real file begins with:
    /// <code>
    /// Notes:
    /// "When exporting your connection data, you may notice that some of the email
    /// addresses are missing..."
    ///
    /// First Name,Last Name,URL,Email Address,Company,Position,Connected On
    /// </code>
    /// So the header is located by content, not by position. Hard-coding "line 4" would break
    /// the moment LinkedIn changes the wording or line count of its own note.
    /// </remarks>
    private static List<Dictionary<string, string>> ReadRows(
        string text,
        string sourcePath,
        ParseContext context)
    {
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalised.Split('\n');

        var headerIndex = Array.FindIndex(lines, l =>
            l.StartsWith(HeaderMarker, StringComparison.OrdinalIgnoreCase));

        if (headerIndex < 0)
        {
            // Fall back to a looser search, in case the columns are reordered.
            headerIndex = Array.FindIndex(lines, l =>
                l.Contains("First Name", StringComparison.OrdinalIgnoreCase)
                && l.Contains("Last Name", StringComparison.OrdinalIgnoreCase));
        }

        if (headerIndex < 0)
        {
            context.Report(
                sourcePath,
                "could not find the CSV header row",
                "expected a line starting with 'First Name,' after the Notes block");
            return [];
        }

        var header = ParseCsvLine(lines[headerIndex]);
        var rows = new List<Dictionary<string, string>>();

        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var values = ParseCsvLine(line);

            if (values.Count == 0)
            {
                continue;
            }

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var column = 0; column < header.Count && column < values.Count; column++)
            {
                row[header[column]] = values[column];
            }

            // A row with no columns at all is footer noise, not a person.
            if (row.Count > 0)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>
    /// Splits one CSV line, honouring quoted fields with embedded commas and doubled quotes.
    /// </summary>
    /// <remarks>
    /// RFC 4180. Required here rather than optional: real <c>Position</c> values look like
    /// <c>"Analyst, Equity Research, Metals and Mining"</c>, and a plain <c>Split(',')</c>
    /// would shred them.
    /// </remarks>
    internal static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder(line.Length);
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];

            if (inQuotes)
            {
                if (character == '"')
                {
                    // A doubled quote inside a quoted field is a literal quote.
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    fields.Add(current.ToString());
                    current.Clear();
                    break;

                default:
                    current.Append(character);
                    break;
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>Parses <c>dd MMM yyyy</c> under the invariant culture.</summary>
    internal static DateTimeOffset? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            value.Trim(),
            DateFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
