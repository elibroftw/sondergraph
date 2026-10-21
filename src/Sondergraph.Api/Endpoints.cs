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
    private readonly Lock _gate = new();
    private Result<Workspace> _current;
    private string? _configured;

    /// <summary>Creates a provider that resolves the workspace on first use.</summary>
    public WorkspaceProvider(IConfiguration configuration)
    {
        _configured = configuration["workspace"]
            ?? Environment.GetEnvironmentVariable(Workspace.EnvironmentVariable);

        _current = Workspace.Locate(_configured);
    }

    /// <summary>The resolved workspace, or a failure explaining how to create one.</summary>
    public Result<Workspace> Resolve()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    /// <summary>The person store, or a failure.</summary>
    public Result<PersonStore> Store()
    {
        var workspace = Resolve();

        return workspace.IsOk
            ? Result.Ok(new PersonStore(workspace.ValueOrThrow()))
            : Result.Fail<PersonStore>(workspace.Code, workspace.Message, workspace.Hint);
    }

    /// <summary>
    /// Points the server at a different workspace.
    /// </summary>
    /// <param name="path">Directory holding a workspace, or a parent of one.</param>
    /// <returns>The newly resolved workspace, or the reason it was refused.</returns>
    /// <remarks>
    /// <para>
    /// The candidate is resolved and validated BEFORE it is adopted, so a bad path leaves the
    /// server on the workspace it was already using. Swapping first and validating second
    /// would leave every request failing after one mistyped directory.
    /// </para>
    /// <para>
    /// Deliberately in-memory only. Persisting the choice would mean the server's data source
    /// changes on the next launch because of something clicked months ago, which is a
    /// surprising and hard-to-undo state. The path stays a startup decision; this endpoint is
    /// for changing it within a session.
    /// </para>
    /// </remarks>
    public Result<Workspace> Switch(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result.Fail<Workspace>(ErrorCode.WorkspaceNotFound, "A workspace path is required.");
        }

        var expanded = Expand(path.Trim());
        var candidate = Workspace.Locate(expanded);

        if (!candidate.IsOk)
        {
            return candidate;
        }

        lock (_gate)
        {
            _configured = expanded;
            _current = candidate;
            return _current;
        }
    }

    /// <summary>Expands <c>~</c> and environment variables in a user-supplied path.</summary>
    private static string Expand(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);

        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = home + expanded[1..];
        }

        return Path.GetFullPath(expanded);
    }

    /// <summary>The path the server was last told to use, for display.</summary>
    public string? Configured
    {
        get
        {
            lock (_gate)
            {
                return _configured;
            }
        }
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

        // Switches the data source for this session. The UI needs this because the workspace
        // was previously a startup-only decision: it came from --workspace or an environment
        // variable, and a user running without either got whatever was in the current
        // directory with no way to point the app elsewhere.
        api.MapPost("/workspace", SwitchWorkspace);

        // Directories the workspace picker can offer, so the user does not have to type an
        // absolute path from memory.
        api.MapGet("/workspace/browse", BrowseWorkspaces);
        api.MapGet("/stats", Stats);
        api.MapGet("/people", ListPeople);
        api.MapGet("/people/{id}", GetPerson);
        api.MapGet("/duplicates", Duplicates);

        // Candidate pairs worth linking, with the evidence for each. This is what makes the
        // merge feature usable: without it a reviewer would have to know both ids already.
        api.MapGet("/links", ListLinkCandidates);

        // Suggestions for ONE contact, which is what the contact panel needs. The
        // whole-workspace scan above is still available for the review queue.
        api.MapGet("/people/{id}/links", PersonLinkCandidates);

        // Notes. GET returns the main note (empty when none); PUT replaces it; DELETE
        // removes it. A body is used rather than a query parameter so note text of any
        // length and encoding survives, including newlines.
        api.MapGet("/people/{id}/note", GetNote);
        api.MapPut("/people/{id}/note", PutNote);
        api.MapDelete("/people/{id}/note", DeleteNote);
        api.MapPost("/people/{id}/note/log", AppendLog);

        // Human-owned fields. PATCH rather than PUT because each endpoint changes one field:
        // a full replace would let a stale client overwrite an edit made elsewhere.
        api.MapPatch("/people/{id}/tier", PatchTier);
        api.MapPatch("/people/{id}/tags", PatchTags);
        api.MapPatch("/people/{id}/locations", PatchLocations);
        api.MapPatch("/people/{id}/preferred", PatchPreferred);

        // The channel list is DERIVED from usage, never stored. This endpoint is what makes
        // the dropdown incapable of holding a leftover: a channel exists exactly while some
        // person has it set.
        api.MapGet("/channels", ListChannels);

        api.MapPatch("/people/{id}/met", PatchHowWeMet);
        api.MapPatch("/people/{id}/name", PatchName);

        // Accounts for platforms with no export. Signal has no contact export at all, so a
        // Signal number or username can only ever be added by hand.
        api.MapPost("/people/{id}/accounts", AddAccount);
        api.MapDelete("/people/{id}/accounts", RemoveAccount);
        api.MapGet("/connections", ListConnections);

        // Places in use, for the location filter. Derived from the people who have one, for
        // the same reason as channels and connections: a stored list keeps entries nobody
        // uses and a derived one cannot.
        api.MapGet("/places", ListPlaces);

        // Guest lists: named sets of people, grouped by recorded gender.
        api.MapGet("/guest-lists", ListGuestLists);
        api.MapGet("/guest-lists/{slug}", GetGuestList);

        // Suggestions for a party: ranked by tier, distance and the gender target. Separate
        // from the guest list itself so accepting a suggestion stays an explicit act.
        api.MapGet("/guest-lists/{slug}/suggestions", GetSuggestions);
        api.MapPatch("/guest-lists/{slug}", PatchGuestList);
        api.MapPatch("/people/{id}/gender", PatchGender);

        // Merging. POST /api/merges creates one; DELETE reverses it. Both are explicit
        // verbs on a resource rather than a "merge" flag on a person, so the result has an
        // id that can be undone.
        api.MapPost("/merges", CreateMerge);
        api.MapDelete("/merges/{mergeId}", UndoMerge);
        api.MapGet("/merges", ListMerges);
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

    private sealed record WorkspaceRequest(string? Path);

    private sealed record GenderRequest(string? Gender);

    private sealed record GuestListRequest(string? Name, string? Notes, List<string>? Members, string? Date, string? Venue);

    private static IResult PatchGender(WorkspaceProvider provider, string id, GenderRequest request)
    {
        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var changed = new PersonEditor(store.ValueOrThrow()).SetGender(id, request.Gender);

        return changed.IsOk
            ? Results.Ok(new { personId = id, gender = request.Gender?.Trim() is { Length: > 0 } g ? g : null })
            : Problem(changed.Code, changed.Message, changed.Hint);
    }

    private static IResult ListGuestLists(WorkspaceProvider provider)
    {
        var workspace = provider.Resolve();

        if (!workspace.IsOk)
        {
            return Problem(workspace.Code, workspace.Message, workspace.Hint);
        }

        var lists = new GuestListStore(workspace.ValueOrThrow()).ListAll();

        if (!lists.IsOk)
        {
            return Problem(lists.Code, lists.Message, lists.Hint);
        }

        var people = provider.Store();

        if (!people.IsOk)
        {
            return Problem(people.Code, people.Message, people.Hint);
        }

        var all = people.ValueOrThrow().ListAll(out _);
        var byId = all.IsOk
            ? all.ValueOrThrow().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase)
            : [];

        return Results.Ok(new
        {
            total = lists.ValueOrThrow().Count,
            lists = lists.ValueOrThrow().Select(l => new
            {
                slug = l.Slug,
                name = l.Name,
                count = l.Count,
                // Unresolved ids are reported, not hidden: a list that silently shrinks is
                // worse than one that says which entries it cannot account for.
                missing = l.Members.Count(id => !byId.ContainsKey(id)),
            }),
        });
    }

    private static IResult GetGuestList(WorkspaceProvider provider, string slug)
    {
        var workspace = provider.Resolve();

        if (!workspace.IsOk)
        {
            return Problem(workspace.Code, workspace.Message, workspace.Hint);
        }

        var loaded = new GuestListStore(workspace.ValueOrThrow()).Load(slug);

        if (!loaded.IsOk)
        {
            return Problem(loaded.Code, loaded.Message, loaded.Hint);
        }

        var people = provider.Store();

        if (!people.IsOk)
        {
            return Problem(people.Code, people.Message, people.Hint);
        }

        var all = people.ValueOrThrow().ListAll(out _);

        if (!all.IsOk)
        {
            return Problem(all.Code, all.Message, all.Hint);
        }

        var byId = all.ValueOrThrow().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var list = loaded.ValueOrThrow();

        var guests = list.Members.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        var missing = list.Members.Where(id => !byId.ContainsKey(id)).ToList();
        var resolved = new ResolvedGuestList(list, guests, missing);

        return Results.Ok(new
        {
            slug = list.Slug,
            name = list.Name,
            notes = list.Notes,
            date = list.Date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            venue = list.Venue,
            count = resolved.Count,
            missing,
            groups = resolved.Groups.Select(g => new
            {
                label = g.Label,
                unrecorded = g.Unrecorded,
                count = g.Count,
                people = g.People.Select(p => new { id = p.Id, name = p.Name }),
            }),
        });
    }

    /// <summary>
    /// Suggests people to invite who are not already on the list.
    /// </summary>
    /// <param name="capacity">How many to suggest. Defaults to filling to 20.</param>
    private static IResult GetSuggestions(WorkspaceProvider provider, string slug, int? capacity)
    {
        var workspace = provider.Resolve();

        if (!workspace.IsOk)
        {
            return Problem(workspace.Code, workspace.Message, workspace.Hint);
        }

        var store = new GuestListStore(workspace.ValueOrThrow());
        var loaded = store.Load(slug);

        if (!loaded.IsOk)
        {
            return Problem(loaded.Code, loaded.Message, loaded.Hint);
        }

        var people = provider.Store();

        if (!people.IsOk)
        {
            return Problem(people.Code, people.Message, people.Hint);
        }

        var all = people.ValueOrThrow().ListAll(out _);

        if (!all.IsOk)
        {
            return Problem(all.Code, all.Message, all.Hint);
        }

        var list = loaded.ValueOrThrow();
        var everyone = all.ValueOrThrow();
        var onList = list.Members.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var gazetteer = new GazetteerStore(workspace.ValueOrThrow()).Load();
        var places = gazetteer.IsOk ? gazetteer.ValueOrThrow() : Gazetteer.Empty;

        var options = new PartyPlanOptions(
            CohortScope.Everyone,
            [],
            Capacity: capacity ?? 20,
            Waves: 1,
            Venue: places.Find(list.Venue),
            Gazetteer: places);

        // Exclude whoever is already invited, so the suggestions are genuinely additive.
        var pool = everyone.Where(p => !onList.Contains(p.Id)).ToList();
        var plan = InvitePlanner.Plan(pool, options);

        return Results.Ok(new
        {
            slug = list.Slug,
            name = list.Name,
            count = list.Members.Count,
            target = list.Members.Count == 0 ? 20 : list.Members.Count,
            // Ordered by weight rather than by wave-and-gender. The planner fills waves by
            // drawing women then men, which is right for building a balanced list but reads
            // badly as a suggestion column: every woman appears above every man, so the
            // reader scrolls past a block of one gender before seeing any of the other. For
            // suggestions, the useful question is "who is the best next pick".
            suggestions = plan.Suggestions
                .OrderByDescending(s => s.Weight)
                .ThenBy(s => s.Person.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(s => new
            {
                id = s.Person.Id,
                name = s.Person.Name,
                tier = PersonDocument.TierToWire(s.Person.Tier),
                tierLabel = PersonDocument.DescribeTier(s.Person.Tier),
                gender = s.Person.Gender,
                distanceKm = s.DistanceKm is { } km ? Math.Round(km) : (double?)null,
                weight = Math.Round(s.Weight, 3),
                sources = s.Person.Sources,
            }),
            notes = plan.Notes,
        });
    }

    /// <summary>Creates a list, or replaces its name, notes and membership.</summary>
    private static IResult PatchGuestList(WorkspaceProvider provider, string slug, GuestListRequest request)
    {
        var workspace = provider.Resolve();

        if (!workspace.IsOk)
        {
            return Problem(workspace.Code, workspace.Message, workspace.Hint);
        }

        var store = new GuestListStore(workspace.ValueOrThrow());
        var name = string.IsNullOrWhiteSpace(request.Name) ? slug : request.Name.Trim();

        var list = new GuestList(
            GuestList.SlugFor(slug),
            name,
            (request.Members ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            request.Notes?.Trim(),
            DateOnly.TryParse(request.Date, System.Globalization.CultureInfo.InvariantCulture, out var date) ? date : null,
            string.IsNullOrWhiteSpace(request.Venue) ? null : request.Venue.Trim());

        var saved = store.Save(list);

        return saved.IsOk
            ? Results.Ok(new { slug = list.Slug, name = list.Name, count = list.Count })
            : Problem(saved.Code, saved.Message, saved.Hint);
    }

    private static IResult SwitchWorkspace(WorkspaceProvider provider, WorkspaceRequest request)
    {
        var switched = provider.Switch(request.Path ?? string.Empty);

        if (!switched.IsOk)
        {
            // A refused switch leaves the server on the workspace it was already using, so
            // the error is informational rather than a broken session.
            return Problem(switched.Code, switched.Message, switched.Hint);
        }

        var workspace = switched.ValueOrThrow();

        return Results.Ok(new
        {
            ok = true,
            root = workspace.Root,
            data = workspace.DataDirectory,
        });
    }

    /// <summary>
    /// Lists a directory's subdirectories so the workspace picker can be navigated.
    /// </summary>
    /// <param name="path">Directory to list; the home directory when omitted.</param>
    /// <remarks>
    /// A picker rather than a bare text box: typing an absolute path from memory is the kind
    /// of thing that fails silently, and the workspace is often several levels down.
    /// </remarks>
    private static IResult BrowseWorkspaces(string? path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = string.IsNullOrWhiteSpace(path) ? home : path.Trim();

        if (target == "~" || target.StartsWith("~/", StringComparison.Ordinal))
        {
            target = home + target[1..];
        }

        string full;

        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Results.Json(
                new { error = "That path is not valid." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Directory.Exists(full))
        {
            return Results.Json(
                new { error = $"'{full}' is not a directory." },
                statusCode: StatusCodes.Status404NotFound);
        }

        var entries = new List<(string Name, string Path, bool IsWorkspace)>();

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(full))
            {
                var name = Path.GetFileName(directory);

                // A workspace is never inside a hidden or generated directory, and listing
                // them makes the picker unusable in a repository or a home directory.
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj")
                {
                    continue;
                }

                entries.Add((name, directory, IsWorkspace(directory)));
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Json(
                new { error = $"Permission denied reading '{full}'." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(new
        {
            path = full,
            parent = Path.GetDirectoryName(full),
            isWorkspace = IsWorkspace(full),
            directories = entries
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new { name = e.Name, path = e.Path, isWorkspace = e.IsWorkspace }),
        });
    }

    /// <summary>Whether a directory is an initialised workspace.</summary>
    /// <remarks>
    /// The marker is <c>data/.sondergraph</c> and it is a DIRECTORY, so this must test
    /// Directory.Exists. Using File.Exists was always false, which made the picker report
    /// every directory as "not a workspace" and hide the button that switches to it.
    ///
    /// Mirrors Workspace.Locate, which walks upwards, so a subdirectory of a workspace is
    /// flagged too - someone browsing into the workspace's own data folder should still be
    /// able to pick it.
    /// </remarks>
    private static bool IsWorkspace(string directory)
    {
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));

            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "data", ".sondergraph")))
                {
                    return true;
                }

                current = current.Parent;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return false;
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
    private static IResult ListPeople(
        WorkspaceProvider provider,
        string? q,
        string? tier,
        string? platform,
        string? place,
        int? limit)
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

        // One parser for the whole filter, so a structured query and the form controls mean
        // exactly the same thing. The separate `tier` and `platform` parameters are folded
        // into the query rather than applied afterwards: two filtering paths would eventually
        // disagree about precedence.
        var query = SearchQuery.Parse(q);

        if (!string.IsNullOrWhiteSpace(tier))
        {
            query = query with { Tiers = [tier.Trim().ToLowerInvariant()] };
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            query = query with
            {
                Platforms = [.. query.Platforms, SearchQuery.ResolvePlatform(platform)],
            };
        }

        if (!string.IsNullOrWhiteSpace(place) && place != "all")
        {
            query = query with { Places = [place.Trim()] };
        }

        if (!query.IsEmpty)
        {
            people = people.Where(query.Matches);
        }

        // Order by usefulness, not alphabetically.
        //
        // A raw ingest produces ~1,600 people, of which only ~230 have a phone number and
        // ~75 are confirmed by more than one source. Alphabetical order buries all of them
        // under scraped Instagram handles, which look like noise and make the app appear to
        // be working badly when it is working well. Sorting by evidence puts the people the
        // user can actually contact at the top, which is what the mass contact view is for.
        // Relationship first: family, then best, close, friend, acquaintance, then unrated.
        // This replaced a contactability ordering (has a phone, then most sources, then has
        // an email). That ordering was a reasonable default before anyone rated a contact,
        // but it buried a best friend under whoever happened to have a phone number. The
        // user stated the intended order explicitly.
        //
        // Reachability decides WITHIN a tier, because when two people are equally close the
        // one you can actually contact is the more useful row. Phone and Signal rank together
        // at the top: both mean a direct, personal route to the person. Email comes next, then
        // accounts that are only a handle, then nothing.
        //
        // Reachability.Rank is a single value rather than a chain of ThenBy calls so the
        // precedence is stated once and cannot be reordered by accident. A chain of four
        // boolean tie-breaks also sorts by "number of qualifying facts" rather than by the
        // intended priority.
        var materialised = people
            .OrderBy(p => RelationshipOrder.Rank(p.Tier))
            .ThenBy(p => Reachability.Rank(p))
            .ThenByDescending(p => p.Sources.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

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

    /// <summary>The request body for writing a note.</summary>
    private sealed record NoteBody(string? Body);

    private static IResult GetNote(WorkspaceProvider provider, string id)
    {
        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var note = new NoteService(store.ValueOrThrow()).GetMainNote(id);

        return note.IsOk
            ? Results.Ok(new { personId = id, body = note.ValueOrThrow() })
            : Problem(note.Code, note.Message, note.Hint);
    }

    private static IResult PutNote(WorkspaceProvider provider, string id, NoteBody request)
    {
        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var saved = new NoteService(store.ValueOrThrow()).SaveMainNote(id, request.Body ?? string.Empty);

        return saved.IsOk
            ? Results.Ok(new { personId = id, saved = true })
            : Problem(saved.Code, saved.Message, saved.Hint);
    }

    private static IResult DeleteNote(WorkspaceProvider provider, string id)
    {
        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var deleted = new NoteService(store.ValueOrThrow()).DeleteMainNote(id);

        return deleted.IsOk
            ? Results.Ok(new { personId = id, deleted = true })
            : Problem(deleted.Code, deleted.Message, deleted.Hint);
    }

    /// <summary>Appends a timestamped entry to the person's note.</summary>
    private static IResult AppendLog(WorkspaceProvider provider, string id, NoteBody request)
    {
        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var appended = new NoteService(store.ValueOrThrow()).AppendLogEntry(id, request.Body ?? string.Empty);

        return appended.IsOk
            ? Results.Ok(new { personId = id, appended = true })
            : Problem(appended.Code, appended.Message, appended.Hint);
    }

    private sealed record MergeRequest(string? Surviving, string? Absorbed, string? Reason);

    private sealed record PreferredRequest(string? Channel);
    private sealed record MetRequest(string? HowWeMet);
    private sealed record NameRequest(string? Name);
    private sealed record AccountRequest(string? Platform, string? Handle, string? ProfileUrl);

    private sealed record TierRequest(string? Tier);
    private sealed record TagsRequest(List<string>? Add, List<string>? Remove);
    private sealed record LocationRequest(string? Place, string? Kind, string? Note, bool Remove);

    private static IResult PatchTier(WorkspaceProvider provider, string id, TierRequest request)
    {
        if (!PersonDocument.TryParseTierInput(request.Tier, out var tier))
        {
            return Results.Json(
                new { error = $"Unknown tier '{request.Tier}'.", code = nameof(ErrorCode.Unknown) },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var store = provider.Store();
        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var changed = new PersonEditor(store.ValueOrThrow()).SetTier(id, tier);

        return changed.IsOk
            ? Results.Ok(new { personId = id, tier = PersonDocument.TierToWire(tier) })
            : Problem(changed.Code, changed.Message, changed.Hint);
    }

    private static IResult PatchTags(WorkspaceProvider provider, string id, TagsRequest request)
    {
        var store = provider.Store();
        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var editor = new PersonEditor(store.ValueOrThrow());
        Result last = Result.Ok();

        foreach (var tag in request.Add ?? [])
        {
            last = editor.AddTag(id, tag);
            if (!last.IsOk) break;
        }

        if (last.IsOk)
        {
            foreach (var tag in request.Remove ?? [])
            {
                last = editor.RemoveTag(id, tag);
                if (!last.IsOk) break;
            }
        }

        if (!last.IsOk)
        {
            return Problem(last.Code, last.Message, last.Hint);
        }

        var reloaded = store.ValueOrThrow().Load(id);

        return reloaded.IsOk
            ? Results.Ok(new { personId = id, tags = reloaded.ValueOrThrow().Tags })
            : Problem(reloaded.Code, reloaded.Message, reloaded.Hint);
    }

    private static IResult PatchLocations(WorkspaceProvider provider, string id, LocationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Place))
        {
            return Results.Json(
                new { error = "A location needs a place.", code = nameof(ErrorCode.Unknown) },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var store = provider.Store();
        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var editor = new PersonEditor(store.ValueOrThrow());
        var kind = PersonDocument.ParseLocationKind(request.Kind);

        var changed = request.Remove
            ? editor.RemoveLocation(id, request.Place)
            : editor.AddLocation(id, request.Place, kind, request.Note);

        if (!changed.IsOk)
        {
            return Problem(changed.Code, changed.Message, changed.Hint);
        }

        var reloaded = store.ValueOrThrow().Load(id);

        return reloaded.IsOk
            ? Results.Ok(new
            {
                personId = id,
                locations = reloaded.ValueOrThrow().Locations.Select(l => new
                {
                    place = l.Place,
                    kind = PersonDocument.LocationToWire(l.Kind),
                    note = l.Note,
                }),
            })
            : Problem(reloaded.Code, reloaded.Message, reloaded.Hint);
    }

    /// <summary>
    /// Channels in use, for the dropdown.
    /// </summary>
    /// <remarks>
    /// Computed from the people on every request rather than cached. With a workspace of a
    /// few thousand contacts that is a cheap scan, and caching a derived list is how it starts
    /// containing entries nothing uses any more — exactly what this design avoids.
    /// </remarks>
    private static IResult ListChannels(WorkspaceProvider provider)
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
        var inUse = PreferredChannels.InUse(people);

        // Every known channel is offered, plus anything already recorded that the list does
        // not name. Offering only channels "in use" made the dropdown nearly empty in a fresh
        // workspace and left no way to choose a channel nobody had picked yet - which, with
        // the create-new flow removed, meant some channels were unreachable.
        var selectable = PreferredChannels.Selectable
            .Concat(inUse)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => PreferredChannels.Selectable.ToList().IndexOf(c) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(c => PreferredChannels.Display(c), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Results.Ok(new
        {
            // Labelled, so the client does not need its own copy of the display names and
            // cannot drift from the server's capitalisation.
            channels = selectable.Select(c => new { value = c, label = PreferredChannels.Display(c) }),

            // Kept for the client's benefit: nothing here is now merely "suggested", since
            // every channel is selectable, so this is always false.
            suggested = false,

            inUse = inUse.Select(c => new
            {
                value = c,
                label = PreferredChannels.Display(c),
                count = people.Count(p => string.Equals(
                    PreferredChannels.Normalise(p.PreferredChannel), c, StringComparison.OrdinalIgnoreCase)),
            }),
        });
    }

    private static IResult PatchPreferred(WorkspaceProvider provider, string id, PreferredRequest request)
    {
        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var personStore = store.ValueOrThrow();
        var editor = new PersonEditor(personStore);

        // Warn, but do not block. A near-match to an existing channel is usually a typo, but
        // it is occasionally a genuinely new channel that happens to resemble one. Blocking
        // would make that impossible.
        var loaded = personStore.ListAll(out _);
        var inUse = loaded.IsOk
            ? PreferredChannels.InUse(loaded.ValueOrThrow())
            : [];

        var suggestion = PreferredChannels.SuggestExisting(request.Channel, inUse);

        var changed = editor.SetPreferredChannel(id, request.Channel);

        if (!changed.IsOk)
        {
            return Problem(changed.Code, changed.Message, changed.Hint);
        }

        return Results.Ok(new
        {
            personId = id,
            channel = PreferredChannels.Normalise(request.Channel) is { Length: > 0 } c ? c : null,
            label = PreferredChannels.Display(request.Channel),
            // Non-null when the written channel resembles one already in use.
            didYouMean = suggestion is null ? null : PreferredChannels.Display(suggestion),
        });
    }

    /// <summary>How-you-know-them values in use, for the suggestion list.</summary>
    /// <summary>
    /// Renames a person.
    /// </summary>
    /// <remarks>
    /// Needed most for records whose name is a platform handle. An Instagram contact arrives
    /// as "abdullah_dabbagh_" because that is all the export contains, and without a rename
    /// the app offers no way to give them a real first and last name.
    ///
    /// The id is deliberately unchanged: it is the directory name that notes, media and merge
    /// records refer to.
    /// </remarks>
    private static IResult PatchName(WorkspaceProvider provider, string id, NameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.Json(
                new { error = "A name cannot be empty.", code = nameof(ErrorCode.Unknown) },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var changed = new PersonEditor(store.ValueOrThrow()).Rename(id, request.Name);

        if (!changed.IsOk)
        {
            return Problem(changed.Code, changed.Message, changed.Hint);
        }

        return Results.Ok(new { personId = id, name = request.Name.Trim() });
    }

    private static IResult AddAccount(WorkspaceProvider provider, string id, AccountRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Platform) || string.IsNullOrWhiteSpace(request.Handle))
        {
            return Results.Json(
                new { error = "An account needs a platform and a handle.", code = nameof(ErrorCode.Unknown) },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var changed = new PersonEditor(store.ValueOrThrow())
            .AddAccount(id, request.Platform, request.Handle, request.ProfileUrl);

        if (!changed.IsOk)
        {
            return Problem(changed.Code, changed.Message, changed.Hint);
        }

        var reloaded = store.ValueOrThrow().Load(id);

        return reloaded.IsOk
            ? Results.Ok(new { personId = id, accounts = ToAccounts(reloaded.ValueOrThrow()) })
            : Problem(reloaded.Code, reloaded.Message, reloaded.Hint);
    }

    private static IResult RemoveAccount(WorkspaceProvider provider, string id, string? platform, string? handle)
    {
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(handle))
        {
            return Results.Json(
                new { error = "Removing an account needs a platform and a handle.", code = nameof(ErrorCode.Unknown) },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var changed = new PersonEditor(store.ValueOrThrow()).RemoveAccount(id, platform, handle);

        if (!changed.IsOk)
        {
            return Problem(changed.Code, changed.Message, changed.Hint);
        }

        var reloaded = store.ValueOrThrow().Load(id);

        return reloaded.IsOk
            ? Results.Ok(new { personId = id, accounts = ToAccounts(reloaded.ValueOrThrow()) })
            : Problem(reloaded.Code, reloaded.Message, reloaded.Hint);
    }

    /// <summary>Projects accounts for the API response.</summary>
    private static IEnumerable<object> ToAccounts(Person person) =>
        person.Accounts.Select(a => new
        {
            platform = a.Platform,
            handle = a.Handle,
            displayName = a.DisplayName,
            profileUrl = a.ProfileUrl,
        });

    /// <summary>Places recorded on any person, most-used first.</summary>
    /// <remarks>
    /// Case is preserved as typed ("San Francisco"), because a place is a name rather than a
    /// key. Deduplication is case-insensitive so "toronto" and "Toronto" do not appear twice,
    /// and the most common spelling wins.
    /// </remarks>
    private static IResult ListPlaces(WorkspaceProvider provider)
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

        var counts = new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in people)
        {
            foreach (var location in person.Locations)
            {
                var place = location.Place.Trim();

                if (place.Length == 0)
                {
                    continue;
                }

                var existing = counts.GetValueOrDefault(place);

                // Keep the first-typed spelling but accumulate the count.
                counts[place] = existing.Display is null
                    ? (place, 1)
                    : (existing.Display, existing.Count + 1);
            }
        }

        var places = counts
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Value.Display, StringComparer.CurrentCultureIgnoreCase)
            .Select(kv => new { value = kv.Value.Display, count = kv.Value.Count })
            .ToList();

        return Results.Ok(new { total = places.Count, places });
    }

    private static IResult ListConnections(WorkspaceProvider provider)
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
        var inUse = ConnectionKinds.InUse(people);

        return Results.Ok(new
        {
            connections = inUse.Count > 0 ? inUse : ConnectionKinds.DefaultSuggestions,
            suggested = inUse.Count == 0,
            inUse = inUse.Select(c => new
            {
                value = c,
                label = ConnectionKinds.Display(c),
                count = people.Count(p => string.Equals(
                    ConnectionKinds.Normalise(p.HowWeMet), c, StringComparison.OrdinalIgnoreCase)),
            }),
        });
    }

    private static IResult PatchHowWeMet(WorkspaceProvider provider, string id, MetRequest request)
    {
        var store = provider.Store();
        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var personStore = store.ValueOrThrow();
        var editor = new PersonEditor(personStore);

        var loaded = personStore.ListAll(out _);
        var inUse = loaded.IsOk ? ConnectionKinds.InUse(loaded.ValueOrThrow()) : [];

        var suggestion = ConnectionKinds.SuggestExisting(request.HowWeMet, inUse);
        var changed = editor.SetHowWeMet(id, request.HowWeMet);

        if (!changed.IsOk)
        {
            return Problem(changed.Code, changed.Message, changed.Hint);
        }

        return Results.Ok(new
        {
            personId = id,
            howWeMet = ConnectionKinds.Normalise(request.HowWeMet) is { Length: > 0 } v ? v : null,
            label = ConnectionKinds.Display(request.HowWeMet),
            didYouMean = suggestion is null ? null : ConnectionKinds.Display(suggestion),
        });
    }

    private static IResult PersonLinkCandidates(WorkspaceProvider provider, string id, double? minScore)
    {
        var store = provider.Store();

        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var personStore = store.ValueOrThrow();
        var person = personStore.Load(id);

        if (!person.IsOk)
        {
            return Problem(person.Code, person.Message, person.Hint);
        }

        var loaded = personStore.ListAll(out _);

        if (!loaded.IsOk)
        {
            return Problem(loaded.Code, loaded.Message, loaded.Hint);
        }

        var candidates = LinkCandidates.ForPerson(
            person.ValueOrThrow(),
            loaded.ValueOrThrow(),
            minScore ?? 0.5);

        return Results.Ok(new
        {
            personId = id,
            total = candidates.Count,
            candidates = candidates.Select(c => new
            {
                id = c.Right.Id,
                name = c.Right.Name,
                sources = c.Right.Sources,
                score = Math.Round(c.Score, 2),
                evidence = c.Evidence.Select(e => e.Detail),
                combined = c.PlatformSummary,
            }),
        });
    }

    private static IResult ListLinkCandidates(WorkspaceProvider provider, double? minScore)
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

        var candidates = LinkCandidates.Find(loaded.ValueOrThrow(), minScore ?? 0.5);

        return Results.Ok(new
        {
            total = candidates.Count,
            candidates = candidates.Select(c => new
            {
                left = new { id = c.Left.Id, name = c.Left.Name, sources = c.Left.Sources },
                right = new { id = c.Right.Id, name = c.Right.Name, sources = c.Right.Sources },
                score = Math.Round(c.Score, 2),
                evidence = c.Evidence.Select(e => new
                {
                    kind = e.Kind.ToString(),
                    detail = e.Detail,
                    confidence = e.Confidence,
                }),
                combined = c.PlatformSummary,
            }),
        });
    }

    private static IResult CreateMerge(WorkspaceProvider provider, MergeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Surviving) || string.IsNullOrWhiteSpace(request.Absorbed))
        {
            return Results.Json(
                new { error = "Both 'surviving' and 'absorbed' ids are required.", code = nameof(ErrorCode.Unknown) },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var store = provider.Store();
        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var workspace = provider.Resolve();
        if (!workspace.IsOk)
        {
            return Problem(workspace.Code, workspace.Message, workspace.Hint);
        }

        var log = MergeLog.Load(workspace.ValueOrThrow().Root);
        if (!log.IsOk)
        {
            return Problem(log.Code, log.Message, log.Hint);
        }

        var merged = new MergeService(store.ValueOrThrow(), log.ValueOrThrow())
            .Merge(request.Surviving, request.Absorbed, request.Reason);

        return merged.IsOk
            ? Results.Ok(new { mergeId = merged.ValueOrThrow().Id })
            : Problem(merged.Code, merged.Message, merged.Hint);
    }

    private static IResult UndoMerge(WorkspaceProvider provider, string mergeId)
    {
        var store = provider.Store();
        if (!store.IsOk)
        {
            return Problem(store.Code, store.Message, store.Hint);
        }

        var workspace = provider.Resolve();
        if (!workspace.IsOk)
        {
            return Problem(workspace.Code, workspace.Message, workspace.Hint);
        }

        var log = MergeLog.Load(workspace.ValueOrThrow().Root);
        if (!log.IsOk)
        {
            return Problem(log.Code, log.Message, log.Hint);
        }

        var undone = new MergeService(store.ValueOrThrow(), log.ValueOrThrow()).Undo(mergeId);

        return undone.IsOk
            ? Results.Ok(new { mergeId, undone = true })
            : Problem(undone.Code, undone.Message, undone.Hint);
    }

    private static IResult ListMerges(WorkspaceProvider provider)
    {
        var workspace = provider.Resolve();
        if (!workspace.IsOk)
        {
            return Problem(workspace.Code, workspace.Message, workspace.Hint);
        }

        var log = MergeLog.Load(workspace.ValueOrThrow().Root);
        if (!log.IsOk)
        {
            return Problem(log.Code, log.Message, log.Hint);
        }

        return Results.Ok(new
        {
            merges = log.ValueOrThrow().Records
                .Where(r => r.Reason?.StartsWith("undo:", StringComparison.Ordinal) != true)
                .Select(r => new
                {
                    id = r.Id,
                    at = r.At,
                    survivorId = r.SurvivorId,
                    absorbedId = r.AbsorbedId,
                    reason = r.Reason,
                    undone = log.ValueOrThrow().Records.Any(u => u.Reason == $"undo:{r.Id}"),
                }),
        });
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
        preferred = person.PreferredChannel,
        preferredLabel = PreferredChannels.Display(person.PreferredChannel),
        howWeMet = person.HowWeMet,
        howWeMetLabel = ConnectionKinds.Display(person.HowWeMet),
        gender = person.Gender,
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
        preferred = person.PreferredChannel,
        preferredLabel = PreferredChannels.Display(person.PreferredChannel),
        howWeMet = person.HowWeMet,
        howWeMetLabel = ConnectionKinds.Display(person.HowWeMet),
        gender = person.Gender,
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
