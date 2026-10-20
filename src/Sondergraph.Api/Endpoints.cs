using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.Api;

/// <summary>
/// Resolves the workspace the API serves.
/// </summary>
/// <remarks>
/// <para>
/// Resolved once and cached, because workspace discovery walks the filesystem and the answer
/// does not change while the process runs. Doing it per request would be both slower and
/// subtly wrong if the working directory ever changed.
/// </para>
/// <para>
/// The API always <em>serves</em> a workspace even when none is configured, so that
/// <c>/api/health</c> can answer and the UI can explain what is missing rather than failing
/// to load at all.
/// </para>
/// </remarks>
public sealed class WorkspaceProvider
{
    private readonly Lazy<Result<Workspace>> _workspace;

    /// <summary>Creates a provider that resolves the workspace on first use.</summary>
    public WorkspaceProvider(IConfiguration configuration)
    {
        var configured = configuration["workspace"]
            ?? Environment.GetEnvironmentVariable(Workspace.EnvironmentVariable);

        _workspace = new Lazy<Result<Workspace>>(() => Workspace.Locate(configured));
    }

    /// <summary>The resolved workspace, or a failure explaining how to create one.</summary>
    public Result<Workspace> Resolve() => _workspace.Value;

    /// <summary>The person store, or a failure.</summary>
    public Result<PersonStore> Store()
    {
        var workspace = Resolve();

        return workspace.IsOk
            ? Result.Ok(new PersonStore(workspace.ValueOrThrow()))
            : Result.Fail<PersonStore>(workspace.Code, workspace.Message, workspace.Hint);
    }
}

/// <summary>
/// The HTTP surface, mapping the same core operations the CLI uses.
/// </summary>
/// <remarks>
/// Thin by design (ADR 0001). No business logic lives here: this maps HTTP to Core calls and
/// Core results to status codes. The mapping is deliberately mechanical so the two shells
/// cannot drift in substance.
/// </remarks>
public static class SondergraphEndpoints
{
    /// <summary>Registers every sondergraph endpoint.</summary>
    public static void MapSondergraphEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", Health);
        api.MapGet("/stats", Stats);
        api.MapGet("/people", ListPeople);
        api.MapGet("/people/{id}", GetPerson);
        api.MapGet("/duplicates", Duplicates);
    }

    /// <summary>
    /// Liveness and workspace status.
    /// </summary>
    /// <remarks>
    /// Deliberately never fails: the handshake design probes this to tell a live server from
    /// a stale pid, so it must answer even when no workspace is configured. Workspace state
    /// is reported in the body instead.
    /// </remarks>
    private static IResult Health(WorkspaceProvider provider)
    {
        var workspace = provider.Resolve();

        return Results.Ok(new
        {
            status = "ok",
            version = SondergraphVersion.Version,
            protocol = SondergraphVersion.Protocol,
            // Both branches must be the same anonymous type, so the failure case carries
            // nulls for the fields it cannot supply rather than a different shape. A client
            // can then read `.workspace.ok` without a type switch.
            workspace = new
            {
                ok = workspace.IsOk,
                root = workspace.IsOk ? workspace.ValueOrThrow().Root : null,
                data = workspace.IsOk ? workspace.ValueOrThrow().DataDirectory : null,
                error = workspace.IsOk ? null : workspace.Message,
                hint = workspace.IsOk ? null : workspace.Hint,
            },
        });
    }

    private static IResult Stats(WorkspaceProvider provider)
    {
        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var loaded = store.ValueOrThrow().ListAll(out _);

        if (!loaded.IsOk)
        {
            return Problem(loaded.Code, loaded.Message, loaded.Hint);
        }

        var people = loaded.ValueOrThrow();

        return Results.Ok(new
        {
            people = people.Count,
            withPhone = people.Count(p => p.Phones.Count > 0),
            withEmail = people.Count(p => p.Emails.Count > 0),
            withLocation = people.Count(p => p.Locations.Count > 0),
            withNotes = people.Count(p => p.Notes.Count > 0),
            multiSource = people.Count(p => p.Sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1),
            platforms = people
                .SelectMany(p => p.Accounts)
                .GroupBy(a => a.Platform, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
            sources = people
                .SelectMany(p => p.Sources)
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
            tiers = people
                .GroupBy(p => PersonDocument.TierToWire(p.Tier))
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
        });
    }

    /// <summary>
    /// Lists people, with server-side filtering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filtering happens here rather than in the browser because the full contact graph is
    /// already loaded server-side and sending 1,600 records for every keystroke would be
    /// wasteful. The query is small enough that a linear scan is fine.
    /// </para>
    /// <para>
    /// Phone search matches on digits, so "555 0100" and "5550100" both find the same person.
    /// </para>
    /// </remarks>
    private static IResult ListPeople(WorkspaceProvider provider, string? q, string? tier, int? limit)
    {
        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var loaded = store.ValueOrThrow().ListAll(out _);

        if (!loaded.IsOk)
        {
            return Problem(loaded.Code, loaded.Message, loaded.Hint);
        }

        IEnumerable<Person> people = loaded.ValueOrThrow();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            var digits = Person.DigitsOnly(needle);

            people = people.Where(p => p.AllIdentifiers().Any(i =>
                i.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (digits.Length >= 3
                    && Person.DigitsOnly(i).Contains(digits, StringComparison.Ordinal))));
        }

        if (!string.IsNullOrWhiteSpace(tier))
        {
            people = people.Where(p =>
                string.Equals(
                    PersonDocument.TierToWire(p.Tier),
                    tier.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        var materialised = people.ToList();
        var total = materialised.Count;

        // Cap the payload. A browser rendering 1,600 rows at once is slow and unhelpful;
        // the UI paginates or narrows the filter instead.
        var capped = limit is > 0 and <= 500 ? limit.Value : 200;

        return Results.Ok(new
        {
            total,
            returned = Math.Min(total, capped),
            people = materialised.Take(capped).Select(ToSummary),
        });
    }

    private static IResult GetPerson(WorkspaceProvider provider, string id)
    {
        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var personStore = store.ValueOrThrow();
        var loaded = personStore.Load(id);

        if (!loaded.IsOk)
        {
            // Fall back to a name or handle match, mirroring the CLI, so a UI link built from
            // a display name still resolves.
            var all = personStore.ListAll(out _);

            if (all.IsOk)
            {
                var matches = all.ValueOrThrow()
                    .Where(p => p.AllIdentifiers().Any(i =>
                        string.Equals(i, id, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (matches.Count == 1)
                {
                    return Results.Ok(ToDetail(matches[0]));
                }
            }

            return Problem(loaded.Code, loaded.Message, loaded.Hint);
        }

        return Results.Ok(ToDetail(loaded.ValueOrThrow()));
    }

    /// <summary>
    /// People who look like duplicates but were not merged.
    /// </summary>
    /// <remarks>
    /// Exposed so the UI can offer a merge flow. Auto-merging is deliberately avoided
    /// (DESIGN.md §4.3) — the user confirms.
    /// </remarks>
    private static IResult Duplicates(WorkspaceProvider provider)
    {
        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var loaded = store.ValueOrThrow().ListAll(out _);

        if (!loaded.IsOk)
        {
            return Problem(loaded.Code, loaded.Message, loaded.Hint);
        }

        var people = loaded.ValueOrThrow();
        var byName = new Dictionary<string, List<Person>>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in people)
        {
            var key = person.Name.Trim();

            if (key.Length == 0)
            {
                continue;
            }

            if (!byName.TryGetValue(key, out var list))
            {
                list = [];
                byName[key] = list;
            }

            list.Add(person);
        }

        var groups = byName
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => new
            {
                name = kv.Key,
                people = kv.Value.Select(ToSummary),
            })
            .ToList();

        return Results.Ok(new
        {
            groups = groups.Count,
            duplicates = groups,
        });
    }

    /// <summary>Projects a person to the list shape the UI needs.</summary>
    private static object ToSummary(Person person) => new
    {
        id = person.Id,
        name = person.Name,
        tier = PersonDocument.TierToWire(person.Tier),
        tierLabel = PersonDocument.DescribeTier(person.Tier),
        phones = person.Phones,
        emails = person.Emails,
        locations = person.Locations.Select(l => new
        {
            place = l.Place,
            kind = PersonDocument.LocationToWire(l.Kind),
            note = l.Note,
        }),
        accounts = person.Accounts.Select(a => new
        {
            platform = a.Platform,
            handle = a.Handle,
            displayName = a.DisplayName,
            profileUrl = a.ProfileUrl,
        }),
        sources = person.Sources,
        tags = person.Tags,
        noteCount = person.Notes.Count,
    };

    /// <summary>Projects a person to the detail shape.</summary>
    private static object ToDetail(Person person) => new
    {
        id = person.Id,
        name = person.Name,
        aliases = person.Aliases,
        tier = PersonDocument.TierToWire(person.Tier),
        tierLabel = PersonDocument.DescribeTier(person.Tier),
        phones = person.Phones,
        emails = person.Emails,
        tags = person.Tags,
        sources = person.Sources,
        locations = person.Locations.Select(l => new
        {
            place = l.Place,
            kind = PersonDocument.LocationToWire(l.Kind),
            note = l.Note,
        }),
        accounts = person.Accounts.Select(a => new
        {
            platform = a.Platform,
            handle = a.Handle,
            displayName = a.DisplayName,
            profileUrl = a.ProfileUrl,
            firstSeen = a.FirstSeen?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        }),
        notes = person.Notes.Select(n => new { fileName = n.FileName, body = n.Body }),
    };

    /// <summary>
    /// Maps a Core error to an HTTP response.
    /// </summary>
    /// <remarks>
    /// The mirror of the CLI's exit-code mapping. Both derive from <see cref="ErrorCode"/>,
    /// so a new code is handled in one place rather than by remembering to update two
    /// switches.
    /// </remarks>
    private static IResult Problem(ErrorCode code, string message, string? hint)
    {
        var status = code switch
        {
            ErrorCode.WorkspaceNotFound => StatusCodes.Status404NotFound,
            ErrorCode.WorkspaceInvalid => StatusCodes.Status500InternalServerError,
            ErrorCode.WorkspaceLocked or ErrorCode.ConcurrentModification => StatusCodes.Status409Conflict,
            ErrorCode.WorkspaceLockTimeout => StatusCodes.Status503ServiceUnavailable,
            ErrorCode.PathOutsideWorkspace => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError,
        };

        return Results.Json(
            new { error = message, hint, code = code.ToString() },
            statusCode: status);
    }
}
