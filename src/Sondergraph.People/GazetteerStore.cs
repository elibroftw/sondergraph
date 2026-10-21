using System.Text.Json;
using Sondergraph.Core;
using Sondergraph.Workspaces;

namespace Sondergraph.People;

/// <summary>
/// Reads and writes <c>data/gazetteer.json</c>.
/// </summary>
/// <remarks>
/// Part of the data directory rather than application state: the coordinates belong to this
/// workspace's places, and a user who moves their workspace should take them along. Plain
/// JSON so it can be hand-edited when a geocoder guesses wrong.
/// </remarks>
public sealed class GazetteerStore
{
    /// <summary>Indented output, reused rather than constructed per call.</summary>
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly Workspace _workspace;

    /// <summary>Creates a store for a workspace.</summary>
    public GazetteerStore(Workspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>Path of the gazetteer file.</summary>
    public string File => Path.Combine(_workspace.DataDirectory, "gazetteer.json");

    /// <summary>Loads the gazetteer; an absent file yields an empty one.</summary>
    public Result<Gazetteer> Load()
    {
        if (!System.IO.File.Exists(File))
        {
            return Result.Ok(Gazetteer.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(File));

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Result.Fail<Gazetteer>(ErrorCode.WorkspaceInvalid, "gazetteer.json is not an array of places.");
            }

            var places = new List<GeoPlace>();

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!entry.TryGetProperty("lat", out var lat) || !lat.TryGetDouble(out var latitude))
                {
                    continue;
                }

                if (!entry.TryGetProperty("lon", out var lon) || !lon.TryGetDouble(out var longitude))
                {
                    continue;
                }

                var source = entry.TryGetProperty("source", out var s) ? s.GetString() : "manual";
                places.Add(new GeoPlace(name, latitude, longitude, source ?? "manual"));
            }

            return Result.Ok(new Gazetteer(places));
        }
        catch (JsonException ex)
        {
            return Result.Fail<Gazetteer>(ErrorCode.WorkspaceInvalid, "gazetteer.json is not valid JSON.", ex.Message);
        }
    }

    /// <summary>Writes the gazetteer, sorted by name so diffs stay small.</summary>
    public Result Save(IEnumerable<GeoPlace> places)
    {
        ArgumentNullException.ThrowIfNull(places);

        var ordered = places
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var payload = ordered.Select(p => new
        {
            name = p.Name,
            lat = Math.Round(p.Latitude, 5),
            lon = Math.Round(p.Longitude, 5),
            source = p.Source,
        });

        System.IO.Directory.CreateDirectory(_workspace.DataDirectory);

        AtomicFile.WriteAllText(File, JsonSerializer.Serialize(payload, WriteOptions) + "\n");

        return Result.Ok();
    }

    /// <summary>Every distinct place named on a person, in the order first seen.</summary>
    public static IReadOnlyList<string> PlacesInUse(IEnumerable<Person> people)
    {
        ArgumentNullException.ThrowIfNull(people);

        var seen = new List<string>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in people)
        {
            foreach (var location in person.Locations)
            {
                var name = location.Place.Trim();

                if (name.Length > 0 && keys.Add(name))
                {
                    seen.Add(name);
                }
            }
        }

        return seen;
    }
}
