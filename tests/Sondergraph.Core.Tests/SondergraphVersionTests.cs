using Sondergraph.Core;

namespace Sondergraph.Core.Tests;

public sealed class SondergraphVersionTests
{
    [Fact]
    public void Version_is_non_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(SondergraphVersion.Version));
    }

    [Fact]
    public void Protocol_is_positive()
    {
        // A zero or negative protocol would make the handshake's version check
        // meaningless (docs/architecture-precedents.md §3).
        Assert.True(SondergraphVersion.Protocol > 0);
    }
}
