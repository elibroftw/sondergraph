using System.Text;
using Sondergraph.Core;

namespace Sondergraph.Ingest.Parsers;

/// <summary>
/// Parses vCard files, supporting both 2.1 and 3.0.
/// </summary>
/// <remarks>
/// <para>
/// A general-purpose parser rather than a platform-specific one: vCard is a standard, and
/// Google Contacts, Apple Contacts and phone exports all produce it. It is registered as a
/// parser so it fits the same seam (ADR 0003), but the format handling is reusable.
/// </para>
/// <para>
/// <b>Version-awareness is the whole point.</b> The dataset contains both versions, and they
/// differ in syntax this parser touches directly:
/// </para>
/// <list type="table">
/// <item><term>2.1</term><description>LF endings, <c>TEL;CELL</c>, inline base64 photos</description></item>
/// <item><term>3.0</term><description>CRLF endings, <c>TEL;TYPE=CELL</c>, separate photo files</description></item>
/// </list>
/// <para>
/// Both parse <em>without error</em> under the wrong assumptions and produce wrong values,
/// which is why this is the most dangerous code in the ingest pipeline.
/// </para>
/// </remarks>
public sealed class VCardParser : IPlatformParser
{
    /// <inheritdoc />
    public string Platform => "vcard";

    /// <inheritdoc />
    public string DisplayName => "vCard contacts";

    /// <inheritdoc />
    public int ParserVersion => 1;

    /// <inheritdoc />
    public bool CanParse(IRawSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (var file in source.EnumerateFiles())
        {
            if (file.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
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

        foreach (var file in source.EnumerateFiles()
            .Where(f => f.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal))
        {
            var raw = source.ReadAllText(file);

            if (raw is null)
            {
                context.Report(file, "could not be read");
                continue;
            }

            // A 0-byte .vcf is normal in Google Takeout, not corruption. Report it at a
            // lower severity by naming it plainly, so it does not look like a failure.
            if (raw.Length == 0)
            {
                context.Report(file, "empty file (0 bytes) - normal for unused contact groups");
                continue;
            }

            foreach (var record in ParseFile(raw, file, source, context))
            {
                yield return record;
            }
        }
    }

    /// <summary>Parses one vCard file's contents.</summary>
    private static IEnumerable<ParsedRecord> ParseFile(
        string raw,
        string sourcePath,
        IRawSource source,
        ParseContext context)
    {
        // Unfold BEFORE anything else. A folded value spans lines, so every later step
        // depends on this having happened. 1808 continuations were observed in a 235-card
        // file, so this is not an edge case.
        var unfolded = Unfold(raw);

        var version = DetectVersion(unfolded);

        foreach (var card in SplitCards(unfolded))
        {
            ParsedRecord? record = null;
            string? failure = null;

            try
            {
                record = ParseCard(card, version, sourcePath, source, context);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or DecoderFallbackException)
            {
                // ADR 0003 rule 4: one bad card must not lose the other 234.
                failure = ex.Message;
            }

            if (record is not null)
            {
                yield return record;
            }
            else if (failure is not null)
            {
                context.Report(sourcePath, "card could not be parsed", failure);
            }
        }
    }

    /// <summary>
    /// Removes line folding: a line beginning with space or tab continues the previous one.
    /// </summary>
    /// <remarks>
    /// Only the single fold marker is removed. Stripping all leading whitespace would corrupt
    /// values that legitimately contain it, and base64 payloads would gain spurious breaks.
    /// </remarks>
    internal static string Unfold(string raw)
    {
        var normalised = raw.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var lines = normalised.Split('\n');
        var output = new StringBuilder(raw.Length);

        foreach (var line in lines)
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                // Continuation: append to the previous line without a separator.
                output.Append(line[1..]);
            }
            else
            {
                if (output.Length > 0)
                {
                    output.Append('\n');
                }

                output.Append(line);
            }
        }

        return output.ToString();
    }

    /// <summary>Reads the declared vCard version, defaulting to 2.1.</summary>
    /// <remarks>
    /// Defaulting to 2.1 when absent matches the older spec, which did not require the
    /// property. Guessing 3.0 would mis-read every TEL parameter in a legacy file.
    /// </remarks>
    internal static string DetectVersion(string unfolded)
    {
        foreach (var line in unfolded.Split('\n'))
        {
            if (line.StartsWith("VERSION:", StringComparison.OrdinalIgnoreCase))
            {
                return line["VERSION:".Length..].Trim();
            }
        }

        return "2.1";
    }

    /// <summary>Splits unfolded text into individual cards.</summary>
    internal static IEnumerable<string> SplitCards(string unfolded)
    {
        var lines = unfolded.Split('\n');
        var card = new StringBuilder();
        var inside = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                inside = true;
                card.Clear();
                continue;
            }

            if (line.StartsWith("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (inside)
                {
                    yield return card.ToString();
                }

                inside = false;
                card.Clear();
                continue;
            }

            if (inside)
            {
                card.Append(line).Append('\n');
            }
        }

        // Tolerate a missing trailing END:VCARD rather than losing the last card.
        if (inside && card.Length > 0)
        {
            yield return card.ToString();
        }
    }

    /// <summary>Parses one card into an observed account.</summary>
    private static ObservedAccount? ParseCard(
        string card,
        string version,
        string sourcePath,
        IRawSource source,
        ParseContext context)
    {
        string? formatted = null;
        string? structuredFirst = null;
        string? structuredLast = null;
        string? org = null;
        string? photoRef = null;
        byte[]? photoBytes = null;
        var phones = new List<string>();
        var emails = new List<string>();
        var groups = new List<string>();
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var declarations = new List<VCardProperty>();

        foreach (var line in card.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var property = VCardProperty.Parse(line);
            if (property is null)
            {
                continue;
            }

            declarations.Add(property);
        }

        // Names come in a second pass because PHOTO needs the resolved name for its file
        // reference in 3.0, and PHOTO may appear before FN in the card.
        foreach (var property in declarations)
        {
            switch (property.Name)
            {
                case "FN":
                    formatted = property.Value.Trim();
                    break;

                case "N":
                    var parts = SplitEscaped(property.Value, ';');
                    if (parts.Count >= 2)
                    {
                        structuredLast = parts[0].Trim();
                        structuredFirst = parts[1].Trim();
                    }
                    else if (parts.Count == 1)
                    {
                        structuredLast = parts[0].Trim();
                    }

                    break;

                case "ORG":
                    org = SplitEscaped(property.Value, ';').FirstOrDefault()?.Trim();
                    break;

                case "TEL":
                    var phone = property.Value.Trim();
                    if (phone.Length > 0)
                    {
                        phones.Add(phone);

                        // Record the parameter form verbatim so a mis-parse is auditable.
                        var kinds = property.ParametersFor("TYPE", version);
                        if (kinds.Count > 0)
                        {
                            extra[$"tel.{phones.Count}.types"] = string.Join(",", kinds);
                        }
                    }

                    break;

                case "EMAIL":
                    var email = property.Value.Trim();
                    if (email.Length > 0)
                    {
                        emails.Add(email);
                    }

                    break;

                case "CATEGORIES":
                    groups.AddRange(SplitEscaped(property.Value, ',').Select(g => g.Trim()));
                    groups.AddRange(SplitEscaped(property.Value, ';').Select(g => g.Trim()));
                    break;

                case "NOTE":
                    extra["note"] = property.Value.Trim();
                    break;

                case "BDAY":
                    extra["birthday"] = property.Value.Trim();
                    break;

                case "ADR":
                    var address = string.Join(", ", SplitEscaped(property.Value, ';')
                        .Select(p => p.Trim())
                        .Where(p => p.Length > 0));
                    if (address.Length > 0)
                    {
                        extra["address"] = address;
                    }

                    break;

                case "PHOTO":
                    ReadPhoto(property, version, source, sourcePath, ref photoRef, ref photoBytes, extra, context);
                    break;
            }
        }

        // Prefer FN, fall back to the structured name. 117 of 432 Takeout cards have
        // neither, and a record with no name is not usable.
        var name = formatted;

        if (string.IsNullOrWhiteSpace(name))
        {
            name = string.Join(' ', new[] { structuredFirst, structuredLast }
                .Where(p => !string.IsNullOrWhiteSpace(p))).Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            context.Report(sourcePath, "card has neither FN nor a usable N");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(org))
        {
            extra["org"] = org;
        }

        extra["vcardVersion"] = version;

        // NativeId: strongest identifier available. Phones and emails are globally unique in
        // practice; a name is not, but it is all some sources provide.
        var (nativeId, strategy) = ChooseIdentifier(name, phones, emails);
        extra["idStrategy"] = strategy;

        // Resolve a sibling photo file when the card only carried a remote URL.
        //
        // Google Takeout writes PHOTO as an https URL, yet also ships the image as a file
        // beside the .vcf named exactly after the contact - including emoji and punctuation,
        // for example "Ramp <emoji>.jpg". That is the offline-capable reference, so prefer it.
        //
        // The lookup is against the source's real file list, never a reconstructed path:
        // display names contain spaces, emoji and mixed case, and rebuilding a filename from
        // FN would break on Unicode normalisation differences that are invisible in a diff.
        if (photoRef is null && photoBytes is null && extra.ContainsKey("photoUrl"))
        {
            var sibling = FindSiblingPhoto(source, sourcePath, name);

            if (sibling is not null)
            {
                photoRef = sibling;
                extra["photoSource"] = "siblingFile";
            }
            else
            {
                extra["photoSource"] = "remoteUrl";
            }
        }

        return new ObservedAccount
        {
            Platform = "vcard",
            NativeId = nativeId,
            SourcePath = sourcePath,
            Handle = name,
            DisplayName = name,
            Phones = phones,
            Emails = emails,
            Groups = groups.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PhotoPath = photoRef,
            PhotoBytes = photoBytes,
            Extra = extra,
        };
    }

    /// <summary>
    /// Picks the best available identifier and names the strategy used.
    /// </summary>
    /// <remarks>
    /// Phone beats email beats name. Recording the strategy matters because a name-derived
    /// id is a guess, and the merge-candidate flow needs to know which records are guesses.
    /// </remarks>
    private static (string Id, string Strategy) ChooseIdentifier(
        string name,
        List<string> phones,
        List<string> emails)
    {
        if (phones.Count > 0)
        {
            var digits = Person.DigitsOnly(phones[0]);
            if (digits.Length >= 7)
            {
                return ($"tel:{digits}", "phone");
            }
        }

        if (emails.Count > 0)
        {
            return ($"email:{emails[0].ToLowerInvariant()}", "email");
        }

        return ($"name:{Person.DeriveId(name)}", "name");
    }

    /// <summary>Handles a PHOTO property in either version's form.</summary>
    private static void ReadPhoto(
        VCardProperty property,
        string version,
        IRawSource source,
        string sourcePath,
        ref string? photoRef,
        ref byte[]? photoBytes,
        Dictionary<string, string> extra,
        ParseContext context)
    {
        var value = property.Value.Trim();

        // 3.0: the value is usually empty, and the image lives in a sibling file named
        // after the contact. The property may also carry a relative path.
        if (value.Length == 0)
        {
            return;
        }

        var encoding = property.ParametersFor("ENCODING", version);
        var isBase64 = encoding.Any(e => e.Equals("b", StringComparison.OrdinalIgnoreCase)
            || e.Equals("BASE64", StringComparison.OrdinalIgnoreCase));

        if (!isBase64 && value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // Google Takeout writes the photo as a remote URL rather than a sibling file,
            // even though sibling .jpg files are also present in the archive. The URL is
            // recorded but never fetched — parsers make no network calls (ADR 0003), and
            // the sibling file, when present, is the offline-capable reference.
            extra["photoUrl"] = value;
            return;
        }

        if (!isBase64 && LooksLikePath(value))
        {
            photoRef = value;
            return;
        }

        // 2.1: inline base64, possibly with a bare media type parameter (JPEG).
        try
        {
            var compact = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
            photoBytes = Convert.FromBase64String(compact);

            // 2.1 encodes the media type as a BARE parameter, so it is keyed by its own name
            // with an empty value list: PHOTO;JPEG;ENCODING=BASE64. Checking only the values
            // misses it entirely, which is why the keys are consulted too.
            extra["photoFormat"] = property.Parameters
                .SelectMany(p => p.Value.Count == 0 ? [p.Key] : p.Value)
                .FirstOrDefault(v => v.Equals("JPEG", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("PNG", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("GIF", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("WEBP", StringComparison.OrdinalIgnoreCase))
                ?? "jpeg";
        }
        catch (FormatException)
        {
            context.Report(sourcePath, "PHOTO value is not valid base64");
        }
    }

    /// <summary>
    /// Finds an image file sitting beside the .vcf, named after the contact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Searches the source's actual file list in the same directory as
    /// <paramref name="vcfPath"/>, so the comparison is against real names rather than a
    /// reconstructed path. This matters: contact names contain emoji, doubled spaces and
    /// mixed case, and Unicode normalisation can differ between what a vCard stores and what
    /// a zip stores while looking identical.
    /// </para>
    /// <para>
    /// Only the immediate directory is searched. Takeout groups photos per contact group
    /// (<c>All Contacts/</c>, <c>My Contacts/</c>), and a global search would attach the wrong
    /// image when the same name appears in two groups.
    /// </para>
    /// </remarks>
    private static string? FindSiblingPhoto(IRawSource source, string vcfPath, string name)
    {
        var directory = Path.GetDirectoryName(vcfPath)?.Replace('\\', '/') ?? string.Empty;
        var prefix = directory.Length == 0 ? string.Empty : directory + "/";

        string[] extensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

        foreach (var file in source.EnumerateFiles())
        {
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = file[prefix.Length..];

            // Only direct siblings, not files in a nested directory.
            if (remainder.Contains('/'))
            {
                continue;
            }

            var extension = extensions.FirstOrDefault(e =>
                remainder.EndsWith(e, StringComparison.OrdinalIgnoreCase));;

            if (extension is null)
            {
                continue;
            }

            var baseName = remainder[..^extension.Length];

            // Normalise both sides before comparing. FormC is what most exporters emit and
            // what the observed Takeout data uses, but normalising guards against a file and
            // a card disagreeing on composition while rendering identically.
            if (string.Equals(
                baseName.Normalize(NormalizationForm.FormC),
                name.Normalize(NormalizationForm.FormC),
                StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    private static bool LooksLikePath(string value) =>
        value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits on a separator, honouring backslash escapes.
    /// </summary>
    /// <remarks>
    /// vCard escapes <c>\,</c> <c>\;</c> <c>\n</c> and <c>\\</c>. A naive split corrupts any
    /// value containing one — 58 escaped commas were observed in a single file.
    /// </remarks>
    internal static List<string> SplitEscaped(string value, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];

            if (character == '\\' && i + 1 < value.Length)
            {
                i++;
                current.Append(value[i] switch
                {
                    'n' or 'N' => '\n',
                    _ => value[i],
                });
                continue;
            }

            if (character == separator)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        parts.Add(current.ToString());
        return parts;
    }
}
