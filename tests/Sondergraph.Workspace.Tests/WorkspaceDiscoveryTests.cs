using Sondergraph.Core;
using Sondergraph.Workspaces;

namespace Sondergraph.Workspaces.Tests;

public sealed class WorkspaceDiscoveryTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>
    /// Discovery and the environment override must not leak between tests.
    /// </summary>
    private static void ClearOverride() =>
        Environment.SetEnvironmentVariable(Workspace.EnvironmentVariable, null);

    private Workspace InitialiseAt(string relative)
    {
        var root = _tree.CreateDirectory(relative);
        var created = Workspace.Create(root);
        Assert.True(created.IsOk, created.Message);
        return created.ValueOrThrow();
    }

    [Fact]
    public void Finds_workspace_in_current_directory()
    {
        ClearOverride();
        var workspace = InitialiseAt("ws");

        var found = Workspace.Locate(workspace.Root);

        Assert.True(found.IsOk, found.Message);
        Assert.Equal(workspace.Root, found.ValueOrThrow().Root);
    }

    [Fact]
    public void Finds_workspace_from_a_nested_subdirectory()
    {
        ClearOverride();
        var workspace = InitialiseAt("ws");
        var nested = _tree.CreateDirectory("ws/data/people/deep/deeper");

        var found = Workspace.Locate(nested);

        Assert.True(found.IsOk, found.Message);
        Assert.Equal(workspace.Root, found.ValueOrThrow().Root);
    }

    [Fact]
    public void Fails_when_no_workspace_exists()
    {
        ClearOverride();
        var lonely = _tree.CreateDirectory("nowhere");

        var found = Workspace.Locate(lonely);

        Assert.False(found.IsOk);
        Assert.Equal(ErrorCode.WorkspaceNotFound, found.Code);
        // The hint is the actionable part; assert it points somewhere useful.
        Assert.Contains("sg init", found.Hint);
    }

    [Fact]
    public void Nearest_workspace_wins_when_nested()
    {
        ClearOverride();
        var outer = InitialiseAt("outer");
        // A workspace inside a workspace: the inner one must win, like a nested git repo.
        var innerRoot = _tree.CreateDirectory("outer/data/inner");
        var inner = Workspace.Create(innerRoot);
        Assert.True(inner.IsOk);

        var found = Workspace.Locate(innerRoot);

        Assert.True(found.IsOk, found.Message);
        Assert.Equal(innerRoot, found.ValueOrThrow().Root);
        Assert.NotEqual(outer.Root, found.ValueOrThrow().Root);
    }

    [Fact]
    public void Environment_override_wins_over_discovery()
    {
        var discovered = InitialiseAt("discovered");
        var overridden = InitialiseAt("overridden");

        try
        {
            Environment.SetEnvironmentVariable(Workspace.EnvironmentVariable, overridden.Root);

            var found = Workspace.Locate(discovered.Root);

            Assert.True(found.IsOk, found.Message);
            Assert.Equal(overridden.Root, found.ValueOrThrow().Root);
        }
        finally
        {
            ClearOverride();
        }
    }

    [Fact]
    public void Environment_override_pointing_nowhere_reports_it()
    {
        var missing = Path.Combine(_tree.Root, "does-not-exist");

        try
        {
            Environment.SetEnvironmentVariable(Workspace.EnvironmentVariable, missing);

            var found = Workspace.Locate(_tree.Root);

            Assert.False(found.IsOk);
            Assert.Equal(ErrorCode.WorkspaceNotFound, found.Code);
            // The message must name the variable, since that is what the user set.
            Assert.Contains(Workspace.EnvironmentVariable, found.Message);
        }
        finally
        {
            ClearOverride();
        }
    }
}
