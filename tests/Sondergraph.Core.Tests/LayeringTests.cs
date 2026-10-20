using System.Reflection;

namespace Sondergraph.Core.Tests;

/// <summary>
/// Guards the architectural rule from ADR 0001: the shells depend on Core, never the
/// reverse.
/// </summary>
/// <remarks>
/// <para>
/// This is a tripwire, not documentation. The shared-core design degrades quietly —
/// someone adds a reference for one small thing and a year later Core depends on
/// ASP.NET and can no longer be hosted by the CLI. A failing test is the cheapest
/// possible way to notice.
/// </para>
/// <para>
/// It asserts on the project's real reference graph by inspecting the assembly
/// references of <c>Sondergraph.Core</c>, so it catches the mistake however it is
/// introduced (csproj edit or IDE "add reference").
/// </para>
/// </remarks>
public sealed class LayeringTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Sondergraph.Cli",
        "Sondergraph.Api",
        // Not a project of ours, but the classic way Core stops being host-agnostic:
        // reaching for a web or console framework.
        "Microsoft.AspNetCore",
    ];

    [Fact]
    public void Core_does_not_reference_any_shell()
    {
        var references = typeof(SondergraphVersion).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        var violations = references
            .Where(name => ForbiddenPrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Sondergraph.Core must not reference a shell or web framework (ADR 0001), "
            + $"but references: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Shells_depend_on_core()
    {
        // The inverse direction must actually hold, or the test above passes vacuously
        // and the shared-core design is not in force at all.
        var core = typeof(SondergraphVersion).Assembly.GetName().Name;

        Assert.Equal("Sondergraph.Core", core);
    }
}
