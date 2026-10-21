using System.Text.Json;
using Sondergraph.Core;
using Sondergraph.People;
using Sondergraph.Workspaces;

namespace Sondergraph.Cli;

/// <summary>
/// Resolves coordinates for the places in use.
/// </summary>
/// <remarks>
/// The only part of the app that touches the network, and it does so once per place rather
/// than per query. Results are written to <c>data/gazetteer.json</c>, which planning then
/// reads offline.
/// </remarks>
internal static class GeoCommands
{
    internal static int Run(string[] args)
    {
        var workspaceArg = ValueAfter(args, "--workspace") ?? ValueAfter(args, "-w");
        var located = Workspace.Locate(workspaceArg);

        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var workspace = located.ValueOrThrow();
        var store = new GazetteerStore(workspace);

        if (args.Contains("--list", StringComparer.Ordinal))
        {
            return List(store);
        }

        // Only resolve when asked: geocoding hits a remote service, and silently doing that
        // whenever the app runs would be both surprising and rude to the provider.
        if (!args.Contains("--resolve", StringComparer.Ordinal))
        {
            return List(store);
        }

        var people = new PersonStore(workspace).ListAll(out _);

        if (!people.IsOk)
        {
            ConsoleRenderer.WriteFailure(people);
            return ExitCode.MapExitCode(people.Code);
        }

        var existing = store.Load();

        if (!existing.IsOk)
        {
            ConsoleRenderer.WriteFailure(existing);
            return ExitCode.MapExitCode(existing.Code);
        }

        var known = existing.ValueOrThrow().Places.ToList();
        var have = known.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = GazetteerStore.PlacesInUse(people.ValueOrThrow())
            .Concat(ValuesAfter(args, "--place"))
            .Where(p => !have.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wanted.Count == 0)
        {
            Console.WriteLine("Every place already has coordinates.");
            return ExitCode.Success;
        }

        // A region appended to the query, not to the stored name. Nominatim ranks by global
        // prominence, so a bare "Waterloo" resolves to Iowa rather than Ontario. The stored
        // key must stay what the user typed, or lookups stop matching their own records.
        var context = ValueAfter(args, "--context");

        Console.WriteLine(
            $"Resolving {wanted.Count} place(s) via OpenStreetMap Nominatim"
            + (context is null ? "…" : $" (context: {context})…"));
        Console.WriteLine();

        var added = 0;

        foreach (var place in wanted)
        {
            var resolved = Lookup(place, context);

            if (resolved is null)
            {
                Console.WriteLine($"  {place,-28} not found");
                continue;
            }

            var shown = Resolved is { Length: > 60 } ? Resolved[..60] + "…" : Resolved;
            Console.WriteLine($"  {place,-28} {resolved.Latitude,9:F4}, {resolved.Longitude,9:F4}   {shown}");
            known.Add(resolved);
            added++;
        }

        var saved = store.Save(known);

        if (!saved.IsOk)
        {
            ConsoleRenderer.WriteFailure(saved);
            return ExitCode.MapExitCode(saved.Code);
        }

        Console.WriteLine();
        Console.WriteLine($"Added {added}. Written to {store.File}");

        return ExitCode.Success;
    }

    private static int List(GazetteerStore store)
    {
        var loaded = store.Load();

        if (!loaded.IsOk)
        {
            ConsoleRenderer.WriteFailure(loaded);
            return ExitCode.MapExitCode(loaded.Code);
        }

        var places = loaded.ValueOrThrow().Places;

        if (places.Count == 0)
        {
            Console.WriteLine("No places have coordinates yet.");
            Console.WriteLine("Run: sg geo --resolve");
            return ExitCode.Success;
        }

        Console.WriteLine($"Places ({places.Count}):");

        foreach (var place in places)
        {
            Console.WriteLine($"  {place.Name,-28} {place.Latitude,9:F4}, {place.Longitude,9:F4}   {place.Source}");
        }

        return ExitCode.Success;
    }

    /// <summary>
    /// Looks a place up with Nominatim.
    /// </summary>
    /// <remarks>
    /// Nominatim's usage policy requires a descriptive User-Agent and at most one request per
    /// second. Both are honoured: the command is a one-off per place, so the delay costs
    /// nothing in practice.
    /// </remarks>
    private static GeoPlace? Lookup(string place, string? context)
    {
        Resolved = null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("sondergraph/0.0.1 (personal contact graph)");

            var query = string.IsNullOrWhiteSpace(context) ? place : $"{place}, {context}";

            var url = "https://nominatim.openstreetmap.org/search?format=json&limit=1&q="
                + Uri.EscapeDataString(query);

            using var response = client.GetAsync(url).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var first = document.RootElement[0];

            if (!first.TryGetProperty("lat", out var lat) || !double.TryParse(lat.GetString(), out var latitude))
            {
                return null;
            }

            if (!first.TryGetProperty("lon", out var lon) || !double.TryParse(lon.GetString(), out var longitude))
            {
                return null;
            }

            // The resolved display name is printed beside the query so a wrong match is
            // visible rather than silently accepted. It is not stored: the gazetteer keys on
            // the name the user typed, and a 200-character OSM string is not a place name.
            Resolved = first.TryGetProperty("display_name", out var d) ? d.GetString() : null;

            // Respect the one-request-per-second policy.
            Thread.Sleep(1100);

            return new GeoPlace(place, latitude, longitude, "nominatim");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The full display name from the last lookup, for reporting only.</summary>
    private static string? Resolved { get; set; }

    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static IEnumerable<string> ValuesAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag && !args[i + 1].StartsWith('-'))
            {
                yield return args[i + 1];
            }
        }
    }
}
