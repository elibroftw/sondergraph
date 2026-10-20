using Sondergraph.Core;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Health probe. The handshake design (docs/architecture-precedents.md §3) has clients
// probe this to distinguish a live server from a stale pid + handshake file, so it must
// stay cheap and must not touch the workspace.
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    version = SondergraphVersion.Version,
    protocol = SondergraphVersion.Protocol,
}));

app.Run();
