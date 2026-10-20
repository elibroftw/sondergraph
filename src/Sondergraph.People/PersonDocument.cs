using System.Globalization;
using System.Text;
using Sondergraph.Core;
using Sondergraph.People.Markdown;

namespace Sondergraph.People;

/// <summary>
/// Serialises a <see cref="Person"/> to and from <c>person.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The file format is the contract from DESIGN.md §4.2: YAML frontmatter for machine-owned
/// fields, markdown body for anything a human wants to read. Every field is on its own line
/// and the order is fixed, so git diffs stay line-based and merges stay sane.
/// </para>
/// <para>
/// <b>Stability matters more than elegance here.</b> These files live in a git repo the user
/// owns. Re-serialising an unchanged person must produce byte-identical output, or every
/// ingest run shows a spurious diff and the user stops trusting the tool.
/// </para>
/// </remarks>
public static class PersonDocument
{
    /// <summary>File name for a person record.</summary>
    public const string FileName = "person.md";

    /// <summary>Renders a person to markdown.</summary>
    public static string Serialize(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);

        var output = new StringBuilder();
        output.Append("---\n");

        output.Append("id: ").Append(Yaml.Scalar(person.Id)).Append('\n');
        output.Append("name: ").Append(Yaml.Scalar(person.Name)).Append('\n');

        Yaml.WriteList(output, "aliases", person.Aliases);
        Yaml.WriteList(output, "phones", person.Phones);
        Yaml.WriteList(output, "emails", person.Emails);
        Yaml.WriteList(output, "tags", person.Tags);
        Yaml.WriteList(output, "sources", person.Sources);

        output.Append("tier: ").Append(TierToWire(person.Tier)).Append('\n');

        if (person.Locations.Count == 0)
        {
            output.Append("locations: []\n");
        }
        else
        {
            output.Append("locations:\n");

            foreach (var location in person.Locations)
            {
                output.Append("  - place: ").Append(Yaml.Scalar(location.Place)).Append('\n');
                output.Append("    kind: ").Append(LocationToWire(location.Kind)).Append('\n');

                if (!string.IsNullOrWhiteSpace(location.Note))
                {
                    output.Append("    note: ").Append(Yaml.Scalar(location.Note)).Append('\n');
                }
            }
        }

        if (person.Accounts.Count == 0)
        {
            output.Append("accounts: []\n");
        }
        else
        {
            output.Append("accounts:\n");

            // Sorted so that ingest order does not cause a spurious diff. This is the
            // single most important determinism decision in the file: accounts arrive from
            // parsers in filesystem order, which is not stable across machines.
            foreach (var account in person.Accounts
                .OrderBy(a => a.Platform, StringComparer.Ordinal)
                .ThenBy(a => a.HandleNormalised, StringComparer.Ordinal))
            {
                output.Append("  - platform: ").Append(Yaml.Scalar(account.Platform)).Append('\n');
                output.Append("    handle: ").Append(Yaml.Scalar(account.Handle)).Append('\n');

                if (!string.IsNullOrWhiteSpace(account.DisplayName))
                {
                    output.Append("    displayName: ").Append(Yaml.Scalar(account.DisplayName)).Append('\n');
                }

                if (!string.IsNullOrWhiteSpace(account.ProfileUrl))
                {
                    output.Append("    profileUrl: ").Append(Yaml.Scalar(account.ProfileUrl)).Append('\n');
                }

                if (account.FirstSeen is { } seen)
                {
                    output.Append("    firstSeen: ").Append(Yaml.FormatDate(seen)).Append('\n');
                }
            }
        }

        if (person.CreatedAt is { } created)
        {
            output.Append("createdAt: ").Append(Yaml.FormatDate(created)).Append('\n');
        }

        if (person.UpdatedAt is { } updated)
        {
            output.Append("updatedAt: ").Append(Yaml.FormatDate(updated)).Append('\n');
        }

        output.Append("---\n");

        // A stable, minimal body. Notes live in notes/*.md so that a long note does not make
        // this file unwieldy, but a short free-text summary is useful when reading the file
        // directly. Only the first note is inlined, and only if it has no file of its own.
        if (person.Notes.Count > 0 && person.Notes[0].FileName == "inline")
        {
            output.Append('\n').Append(person.Notes[0].Body.TrimEnd()).Append('\n');
        }

        return output.ToString();
    }

    /// <summary>Parses a person from markdown.</summary>
    public static Result<Person> Deserialize(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var (scalars, lists) = Yaml.ParseFrontmatter(markdown);

        if (!scalars.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return Result.Fail<Person>(
                ErrorCode.WorkspaceInvalid,
                "Person document has no 'id' in its frontmatter.",
                "Every person.md needs an id; it is the directory name.");
        }

        if (!scalars.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            return Result.Fail<Person>(
                ErrorCode.WorkspaceInvalid,
                $"Person '{id}' has no 'name' in its frontmatter.");
        }

        var person = new Person
        {
            Id = id,
            Name = name,
            Aliases = lists.GetValueOrDefault("aliases", []),
            Phones = lists.GetValueOrDefault("phones", []),
            Emails = lists.GetValueOrDefault("emails", []),
            Tags = lists.GetValueOrDefault("tags", []),
            Sources = lists.GetValueOrDefault("sources", []),
            Tier = ParseTier(scalars.GetValueOrDefault("tier")),
            CreatedAt = Yaml.ParseDate(scalars.GetValueOrDefault("createdAt")),
            UpdatedAt = Yaml.ParseDate(scalars.GetValueOrDefault("updatedAt")),
        };

        var note = ExtractBody(markdown);
        if (!string.IsNullOrWhiteSpace(note))
        {
            person = person with { Notes = [new ContactNote("inline", note)] };
        }

        var accounts = ParseAccounts(markdown);
        var locations = ParseLocations(markdown);

        return Result.Ok(person with { Accounts = accounts, Locations = locations });
    }

    /// <summary>
    /// Extracts the markdown body following the frontmatter fence.
    /// </summary>
    private static string ExtractBody(string markdown)
    {
        var normalised = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
        {
            return normalised.Trim();
        }

        var end = normalised.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        return end < 0 ? string.Empty : normalised[(end + 5)..].Trim();
    }

    /// <summary>
    /// Parses the <c>accounts</c> block.
    /// </summary>
    /// <remarks>
    /// Hand-parsed rather than deserialised, because the enclosing document is markdown and
    /// the accounts block is a nested list. The parser is deliberately tolerant of missing
    /// optional keys — a hand-edited file should load with a warning rather than fail hard.
    /// </remarks>
    private static List<PlatformAccount> ParseAccounts(string markdown)
    {
        var accounts = new List<PlatformAccount>();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        string? platform = null;
        string? handle = null;
        string? displayName = null;
        string? profileUrl = null;
        DateOnly? firstSeen = null;
        var inAccounts = false;

        void Flush()
        {
            if (platform is not null && handle is not null)
            {
                accounts.Add(PlatformAccount.Create(platform, handle, displayName, profileUrl, firstSeen));
            }

            platform = handle = displayName = profileUrl = null;
            firstSeen = null;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("accounts:", StringComparison.Ordinal))
            {
                inAccounts = line.Trim() != "accounts: []";
                continue;
            }

            if (!inAccounts)
            {
                continue;
            }

            // A new top-level key ends the block.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                Flush();
                inAccounts = false;
                continue;
            }

            var trimmed = line.Trim();

            if (trimmed.StartsWith("- platform:", StringComparison.Ordinal))
            {
                Flush();
                platform = Yaml.Unquote(trimmed["- platform:".Length..].Trim());
                continue;
            }

            if (trimmed.StartsWith("handle:", StringComparison.Ordinal))
            {
                handle = Yaml.Unquote(trimmed["handle:".Length..].Trim());
                continue;
            }

            if (trimmed.StartsWith("displayName:", StringComparison.Ordinal))
            {
                displayName = Yaml.Unquote(trimmed["displayName:".Length..].Trim());
                continue;
            }

            if (trimmed.StartsWith("profileUrl:", StringComparison.Ordinal))
            {
                profileUrl = Yaml.Unquote(trimmed["profileUrl:".Length..].Trim());
                continue;
            }

            if (trimmed.StartsWith("firstSeen:", StringComparison.Ordinal))
            {
                firstSeen = Yaml.ParseDate(Yaml.Unquote(trimmed["firstSeen:".Length..].Trim()));
            }
        }

        Flush();
        return accounts;
    }

    /// <summary>Parses the <c>locations</c> block.</summary>
    private static List<ContactLocation> ParseLocations(string markdown)
    {
        var locations = new List<ContactLocation>();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        string? place = null;
        var kind = LocationKind.Other;
        string? note = null;
        var inLocations = false;

        void Flush()
        {
            if (place is not null)
            {
                locations.Add(new ContactLocation(place, kind, note));
            }

            place = null;
            kind = LocationKind.Other;
            note = null;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("locations:", StringComparison.Ordinal))
            {
                inLocations = line.Trim() != "locations: []";
                continue;
            }

            if (!inLocations)
            {
                continue;
            }

            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                Flush();
                inLocations = false;
                continue;
            }

            var trimmed = line.Trim();

            if (trimmed.StartsWith("- place:", StringComparison.Ordinal))
            {
                Flush();
                place = Yaml.Unquote(trimmed["- place:".Length..].Trim());
                continue;
            }

            if (trimmed.StartsWith("kind:", StringComparison.Ordinal))
            {
                kind = ParseLocationKind(Yaml.Unquote(trimmed["kind:".Length..].Trim()));
                continue;
            }

            if (trimmed.StartsWith("note:", StringComparison.Ordinal))
            {
                note = Yaml.Unquote(trimmed["note:".Length..].Trim());
            }
        }

        Flush();
        return locations;
    }

    /// <summary>Wire form for a tier. Stable; changing these is a breaking change.</summary>
    public static string TierToWire(ContactTier tier) => tier switch
    {
        ContactTier.Acquaintance => "acquaintance",
        ContactTier.Friend => "friend",
        ContactTier.Close => "close",
        ContactTier.Best => "best",
        ContactTier.Family => "family",
        _ => "unknown",
    };

    /// <summary>Parses a wire-form tier, defaulting to <see cref="ContactTier.Unknown"/>.</summary>
    public static ContactTier ParseTier(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "acquaintance" => ContactTier.Acquaintance,
        "friend" => ContactTier.Friend,
        "close" => ContactTier.Close,
        "best" => ContactTier.Best,
        "family" => ContactTier.Family,
        _ => ContactTier.Unknown,
    };

    /// <summary>Wire form for a location kind.</summary>
    public static string LocationToWire(LocationKind kind) => kind switch
    {
        LocationKind.Home => "home",
        LocationKind.Work => "work",
        _ => "other",
    };

    /// <summary>Parses a wire-form location kind.</summary>
    public static LocationKind ParseLocationKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "home" => LocationKind.Home,
        "work" => LocationKind.Work,
        _ => LocationKind.Other,
    };

    /// <summary>
    /// Formats a tier for display in the terminal and UI.
    /// </summary>
    public static string DescribeTier(ContactTier tier) => tier switch
    {
        ContactTier.Acquaintance => "acquaintance",
        ContactTier.Friend => "friend",
        ContactTier.Close => "close friend",
        ContactTier.Best => "best friend",
        ContactTier.Family => "family",
        _ => "-",
    };

    /// <summary>Parses a tier from user input, accepting common synonyms.</summary>
    public static bool TryParseTierInput(string? input, out ContactTier tier)
    {
        switch (input?.Trim().ToLowerInvariant())
        {
            case "a" or "acq" or "acquaintance":
                tier = ContactTier.Acquaintance;
                return true;
            case "f" or "friend":
                tier = ContactTier.Friend;
                return true;
            case "c" or "close" or "closefriend" or "close-friend":
                tier = ContactTier.Close;
                return true;
            case "b" or "best" or "bestfriend" or "best-friend":
                tier = ContactTier.Best;
                return true;
            case "fam" or "family":
                tier = ContactTier.Family;
                return true;
            case "u" or "unknown" or "none":
                tier = ContactTier.Unknown;
                return true;
            default:
                tier = ContactTier.Unknown;
                return false;
        }
    }

    /// <summary>
    /// Renders a one-line summary for list output.
    /// </summary>
    public static string Describe(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);

        var parts = new List<string>();

        if (person.Tier != ContactTier.Unknown)
        {
            parts.Add(DescribeTier(person.Tier));
        }

        if (person.Accounts.Count > 0)
        {
            parts.Add(string.Join(", ", person.Accounts
                .Select(a => a.Platform)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)));
        }

        if (person.Phones.Count > 0)
        {
            parts.Add(person.Phones.Count == 1 ? "phone" : $"{person.Phones.Count} phones");
        }

        if (person.Emails.Count > 0)
        {
            parts.Add(person.Emails.Count == 1 ? "email" : $"{person.Emails.Count} emails");
        }

        return parts.Count == 0
            ? string.Empty
            : string.Join(" · ", parts);
    }

    /// <summary>Formats a count for display, e.g. <c>1 person</c> / <c>3 people</c>.</summary>
    public static string People(int count) =>
        count == 1 ? "1 person" : $"{count.ToString(CultureInfo.InvariantCulture)} people";
}
