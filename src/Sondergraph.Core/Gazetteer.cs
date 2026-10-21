namespace Sondergraph.Core;

/// <summary>A place with coordinates.</summary>
/// <param name="Name">Canonical display name, e.g. <c>Toronto</c>.</param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
/// <param name="Source">How the coordinates were obtained, e.g. <c>nominatim</c>.</param>
public sealed record GeoPlace(string Name, double Latitude, double Longitude, string Source = "manual")
{
    /// <summary>Great-circle distance to another place, in kilometres.</summary>
    public double DistanceKm(GeoPlace other)
    {
        ArgumentNullException.ThrowIfNull(other);

        const double EarthRadiusKm = 6371.0088;

        var lat1 = Latitude * Math.PI / 180;
        var lat2 = other.Latitude * Math.PI / 180;
        var deltaLat = (other.Latitude - Latitude) * Math.PI / 180;
        var deltaLon = (other.Longitude - Longitude) * Math.PI / 180;

        // Haversine: accurate to a few metres at city scale, which is far more than a
        // distance-decayed weight needs.
        var a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2)
            + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);

        return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

/// <summary>
/// Looks up coordinates for place names.
/// </summary>
/// <remarks>
/// <para>
/// A plain lookup table, not a geocoder. Coordinates are resolved once - by
/// <c>sg geo resolve</c>, which calls a network service - and written into
/// <c>data/gazetteer.json</c>. Planning a party then reads only that file: no network call,
/// no latency, and the same plan on every machine.
/// </para>
/// <para>
/// Matching is deliberately forgiving on case and punctuation, because a place is typed by
/// hand. "toronto", "Toronto" and "Toronto " all find the same entry.
/// </para>
/// </remarks>
public sealed class Gazetteer
{
    private readonly Dictionary<string, GeoPlace> _byKey;

    /// <summary>Creates a gazetteer from a set of places.</summary>
    public Gazetteer(IEnumerable<GeoPlace> places)
    {
        ArgumentNullException.ThrowIfNull(places);

        Places = places.ToList();
        _byKey = new Dictionary<string, GeoPlace>(StringComparer.OrdinalIgnoreCase);

        foreach (var place in Places)
        {
            // First entry wins, so a hand-corrected coordinate is not overwritten by a
            // duplicate that happens to sort later.
            _byKey.TryAdd(Key(place.Name), place);
        }
    }

    /// <summary>An empty gazetteer.</summary>
    public static Gazetteer Empty { get; } = new([]);

    /// <summary>Every known place.</summary>
    public IReadOnlyList<GeoPlace> Places { get; }

    /// <summary>Finds a place, or null.</summary>
    public GeoPlace? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return _byKey.GetValueOrDefault(Key(name));
    }

    /// <summary>
    /// Distance from a party location to where someone lives, or null when either is unknown.
    /// </summary>
    /// <remarks>
    /// Takes the closest of a person's locations: someone with a home in Toronto and a work
    /// address in Waterloo is near a Toronto party.
    /// </remarks>
    public double? Distance(Person person, GeoPlace party)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(party);

        double? closest = null;

        foreach (var location in person.Locations)
        {
            var place = Find(location.Place);

            if (place is null)
            {
                continue;
            }

            var distance = party.DistanceKm(place);

            if (closest is null || distance < closest)
            {
                closest = distance;
            }
        }

        return closest;
    }

    private static string Key(string name) =>
        new(name.Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());
}
