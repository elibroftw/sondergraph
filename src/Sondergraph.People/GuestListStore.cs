using Sondergraph.Core;
using Sondergraph.People.Markdown;
using Sondergraph.Workspaces;

namespace Sondergraph.People;

/// <summary>
/// Reads and writes guest lists as markdown files in the workspace.
/// </summary>
/// <remarks>
/// <para>
/// One file per list, under <c>data/guest-lists/</c>, in the same shape as person records so
/// the whole workspace stays greppable and hand-editable. A guest list is a document the user
/// will want to read outside this app, so it opens with the list itself as a checklist rather
/// than a wall of ids.
/// </para>
/// </remarks>
public sealed class GuestListStore
{
    /// <summary>Directory holding the list files, relative to the data directory.</summary>
    public const string DirectoryName = "guest-lists";

    private readonly Workspace _workspace;

    /// <summary>Creates a store for a workspace.</summary>
    public GuestListStore(Workspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>Directory holding the lists.</summary>
    public string Directory => Path.Combine(_workspace.DataDirectory, DirectoryName);

    /// <summary>Lists every guest list, ordered by name.</summary>
    public Result<IReadOnlyList<GuestList>> ListAll()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return Result.Ok<IReadOnlyList<GuestList>>([]);
        }

        var lists = new List<GuestList>();

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.md").Order(StringComparer.Ordinal))
        {
            var parsed = ReadFile(file);

            if (parsed.IsOk)
            {
                lists.Add(parsed.ValueOrThrow());
            }
        }

        return Result.Ok<IReadOnlyList<GuestList>>(
            lists.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    /// <summary>Loads one list by slug.</summary>
    public Result<GuestList> Load(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Result.Fail<GuestList>(ErrorCode.Unknown, "A guest list needs a name.");
        }

        var file = Path.Combine(Directory, GuestList.SlugFor(slug) + ".md");

        if (!File.Exists(file))
        {
            return Result.Fail<GuestList>(
                ErrorCode.Unknown,
                $"No guest list called '{slug}'.",
                "Run 'sg guest list' to see the existing lists.");
        }

        return ReadFile(file);
    }

    /// <summary>Creates or replaces a list.</summary>
    public Result Save(GuestList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        System.IO.Directory.CreateDirectory(Directory);

        var file = Path.Combine(Directory, list.Slug + ".md");
        var content = Serialise(list, out var members);

        AtomicFile.WriteAllText(file, content);

        return Result.Ok();
    }

    /// <summary>
    /// Writes a list, resolving person names for the readable checklist.
    /// </summary>
    /// <remarks>
    /// The id is what the app reads back; the name beside it is for a human opening the file.
    /// Storing only ids would make the file unreadable, and storing only names would break the
    /// link when a person is renamed or merged.
    /// </remarks>
    private string Serialise(GuestList list, out IReadOnlyList<string> members)
    {
        var people = new PersonStore(_workspace).ListAll(out _);

        var byId = people.IsOk
            ? people.ValueOrThrow().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase)
            : [];

        members = list.Members;

        var builder = new System.Text.StringBuilder();

        builder.Append("---\n");
        builder.Append("name: ").Append(Yaml.Scalar(list.Name)).Append('\n');

        if (!string.IsNullOrWhiteSpace(list.Notes))
        {
            builder.Append("notes: ").Append(Yaml.Scalar(list.Notes)).Append('\n');
        }

        builder.Append("members:\n");

        foreach (var id in list.Members)
        {
            builder.Append("  - ").Append(Yaml.Scalar(id)).Append('\n');
        }

        builder.Append("---\n\n");

        // The checklist is a view of the front matter, regenerated on every save. Editing it
        // by hand is not read back: the ids above are the source of truth, because a name can
        // change and an id cannot.
        builder.Append("# ").Append(list.Name).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(list.Notes))
        {
            builder.Append(list.Notes).Append("\n\n");
        }

        builder.Append("- [ ] ").Append(list.Members.Count).Append(" guests\n\n");

        foreach (var group in list.Members
            .Select(id => byId.GetValueOrDefault(id))
            .Where(p => p is not null)
            .Select(p => p!)
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Gender) ? null : p.Gender.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key is null ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            builder.Append("## ").Append(group.Key ?? "not recorded").Append('\n');

            foreach (var person in group.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                builder.Append("- [ ] ").Append(person.Name).Append('\n');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Reads one file.</summary>
    private static Result<GuestList> ReadFile(string file)
    {
        var text = File.ReadAllText(file);

        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return Result.Fail<GuestList>(ErrorCode.Unknown, $"{Path.GetFileName(file)} has no front matter.");
        }

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);

        if (end < 0)
        {
            return Result.Fail<GuestList>(
                ErrorCode.Unknown,
                $"{Path.GetFileName(file)} has unterminated front matter.");
        }

        var header = text[3..end];
        string? name = null;
        string? notes = null;
        var members = new List<string>();
        var inMembers = false;

        foreach (var rawLine in header.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.TrimStart();

            // A list item belongs to whichever key preceded it; only "members" is a list
            // here, so items under any other key are ignored rather than misfiled.
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (inMembers && Unscalar(trimmed[2..]) is { Length: > 0 } id)
                {
                    members.Add(id);
                }

                continue;
            }

            var colon = trimmed.IndexOf(':', StringComparison.Ordinal);

            if (colon <= 0)
            {
                inMembers = false;
                continue;
            }

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..];

            switch (key)
            {
                case "name":
                    name = Unscalar(value);
                    inMembers = false;
                    break;

                case "notes":
                    notes = Unscalar(value);
                    inMembers = false;
                    break;

                case "members":
                    inMembers = true;
                    break;

                default:
                    inMembers = false;
                    break;
            }
        }

        var slug = Path.GetFileNameWithoutExtension(file);

        return Result.Ok(new GuestList(
            slug,
            string.IsNullOrWhiteSpace(name) ? slug : name,
            members,
            string.IsNullOrWhiteSpace(notes) ? null : notes));
    }

    private static string Unscalar(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}
