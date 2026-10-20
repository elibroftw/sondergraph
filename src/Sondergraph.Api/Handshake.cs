using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Sondergraph.Core;

namespace Sondergraph.Api;

/// <summary>
/// Reports the server's chosen port to a launcher that spawned it.
/// </summary>
/// <remarks>
/// <para>
/// The desktop shell starts this process with <c>--handshake</c> and reads one JSON object
/// per line from stdout. It needs the port because the port is ephemeral by design — the
/// kernel picks a free one, so a fixed port would collide with whatever else is running
/// (docs/architecture-precedents.md §3).
/// </para>
/// <para>
/// Three events, deliberately: a launch that fails can then say <em>how far it got</em>
/// rather than leaving the user with a blank window and no diagnosis.
/// </para>
/// <list type="bullet">
/// <item><c>server.listening</c> — bound, port known, not necessarily ready to serve.</item>
/// <item><c>server.ready</c> — safe to navigate the webview here.</item>
/// <item><c>server.error</c> — with a machine-readable code and a human message.</item>
/// </list>
/// <para>
/// Emitted only when <c>--handshake</c> is passed. A person running the server by hand does
/// not want JSON interleaved with the request log.
/// </para>
/// </remarks>
internal static class Handshake
{
    /// <summary>Argument that requests the handshake.</summary>
    internal const string Flag = "--handshake";

    /// <summary>Whether the handshake was requested.</summary>
    internal static bool IsRequested(string[] args) =>
        args.Contains(Flag, StringComparer.Ordinal);

    /// <summary>
    /// Wires the handshake to fire once the server has started.
    /// </summary>
    public static void Attach(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                Emit(app);
            }
            catch (Exception ex)
            {
                // If the handshake itself fails, the launcher must be told: otherwise it
                // waits for a line that will never arrive and appears to hang.
                EmitError("handshake-failed", ex.Message);
            }
        });
    }

    private static void Emit(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        var address = addresses?.Addresses.FirstOrDefault();

        if (address is null)
        {
            EmitError("no-address", "Kestrel reported no listening address.");
            return;
        }

        var uri = new Uri(address);
        var workspace = app.Services.GetRequiredService<WorkspaceProvider>().Resolve();

        Write(new
        {
            @event = "server.listening",
            addr = $"{uri.Host}:{uri.Port}",
            pid = Environment.ProcessId,
            protocol = SondergraphVersion.Protocol,
        });

        Write(new
        {
            @event = "server.ready",
            url = address,
            workspace = workspace.IsOk ? workspace.ValueOrThrow().Root : null,
            workspaceOk = workspace.IsOk,
            workspaceError = workspace.IsOk ? null : workspace.Message,
        });
    }

    private static void EmitError(string code, string message) =>
        Write(new { @event = "server.error", code, message });

    /// <summary>
    /// Writes one JSON object and flushes.
    /// </summary>
    /// <remarks>
    /// The flush is essential: stdout is redirected to a pipe when spawned, which is fully
    /// buffered. Without an explicit flush the launcher blocks until the buffer happens to
    /// fill, which for a process that stays alive looks exactly like a hang.
    /// </remarks>
    private static void Write(object payload)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(payload));
        Console.Out.Flush();
    }
}
