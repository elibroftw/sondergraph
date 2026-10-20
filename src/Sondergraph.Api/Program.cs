using Sondergraph.Api;
using Sondergraph.Core;

// Resolve content relative to THIS assembly, not the current working directory.
//
// When the API runs as a desktop sidecar it is started by the Tauri shell, whose working
// directory is wherever the shell happens to be. ASP.NET's default content root is the CWD,
// so it would look for wwwroot next to the shell - find nothing - and serve 404 for the UI
// while reporting a perfectly healthy server.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Bind loopback on an ephemeral port by default. The handshake design in
// docs/architecture-precedents.md §3 depends on a dynamic port, and the desktop shell
// reads it back from the addresses feature after startup.
//
// Note: ListenLocalhost(0) throws on .NET 10 - "Dynamic port binding is not supported when
// binding to localhost" - so this must be Listen(IPAddress.Loopback, 0).
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(System.Net.IPAddress.Loopback, ApiOptions.ResolvePort(builder.Configuration));
});

// JSON: relaxed escaping so non-ASCII names render readably rather than as \uXXXX. Contact
// names routinely contain accents and emoji, and escaped output is unreadable in a browser
// network tab, which is where this gets debugged.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    options.SerializerOptions.WriteIndented = false;
});

builder.Services.AddSingleton<WorkspaceProvider>();

var app = builder.Build();

// Serve the built Svelte app from wwwroot when it exists, so the UI and the API share an
// origin and no CORS configuration is needed at all (docs/architecture-precedents.md §3).
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapSondergraphEndpoints();

// SPA fallback: any unmatched non-API path returns index.html so client-side routing works
// on a refresh. Deliberately after the API routes so it cannot swallow them.
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { error = "not found" });
        return;
    }

    var index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");

    if (File.Exists(index))
    {
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(index);
        return;
    }

    // No UI built yet: say so usefully rather than returning a bare 404.
    context.Response.ContentType = "text/plain";
    await context.Response.WriteAsync(
        "sondergraph API is running, but no web UI is built.\n"
        + "Build it with: pnpm --dir web build\n"
        + $"API is at http://127.0.0.1:{context.Connection.LocalPort}/api/health\n");
});

// Report the chosen port to a launcher that spawned us (the desktop shell). Emitted only
// when --handshake is passed, so running the server by hand prints nothing extra.
if (Handshake.IsRequested(args))
{
    Handshake.Attach(app);
}

app.Run();

/// <summary>
/// Resolves the port to bind from configuration or the environment.
/// </summary>
/// <remarks>
/// 0 means "let the kernel choose", which is the default and what the launcher expects.
/// A fixed port is only needed for manual browser access during development.
/// </remarks>
internal static class ApiOptions
{
    internal static int ResolvePort(IConfiguration configuration)
    {
        var configured = Environment.GetEnvironmentVariable("SONDERGRAPH_PORT")
            ?? configuration["port"];

        return int.TryParse(configured, out var port) && port is >= 0 and <= 65535
            ? port
            : 0;
    }
}
