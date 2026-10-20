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
var requestedPort = ApiOptions.ResolvePort(args, builder.Configuration);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(System.Net.IPAddress.Loopback, requestedPort);
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

// Serve the built Svelte app, so the UI and the API share an origin and no CORS
// configuration is needed at all (docs/architecture-precedents.md §3).
//
// --web-root exists for development: it points at the repository's build output directly, so
// the desktop shell does not need a copy of wwwroot next to the sidecar. That copy was a
// manual step which any rebuild silently invalidated - the window then reported "no web UI is
// built" while the build output was perfectly fine.
var webRoot = ApiOptions.ResolveWebRoot(args);

if (webRoot is not null)
{
    if (!Directory.Exists(webRoot))
    {
        Console.Error.WriteLine(
            $"warning: --web-root '{webRoot}' does not exist; the UI will not be served.");
    }
    else
    {
        var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
            Path.GetFullPath(webRoot));

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
    }
}
else
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

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
    /// <summary>
    /// Resolves the port from <c>--port</c>, then the environment, then configuration.
    /// </summary>
    /// <remarks>
    /// A command-line flag is supported because the desktop shell passes one in development,
    /// and requiring an environment variable for that would make the shell's behaviour
    /// depend on how it was launched.
    /// </remarks>
    internal static int ResolvePort(string[] args, IConfiguration configuration)
    {
        var configured = ValueAfter(args, "--port")
            ?? Environment.GetEnvironmentVariable("SONDERGRAPH_PORT")
            ?? configuration["port"];

        return int.TryParse(configured, out var port) && port is >= 0 and <= 65535
            ? port
            : 0;
    }

    /// <summary>Resolves an explicit web root from <c>--web-root</c>, if given.</summary>
    internal static string? ResolveWebRoot(string[] args)
    {
        var configured = ValueAfter(args, "--web-root");

        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    /// <summary>Returns the argument following <paramref name="flag"/>, or null.</summary>
    private static string? ValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
