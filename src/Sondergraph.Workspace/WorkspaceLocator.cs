namespace Sondergraph.Workspace;

/// <summary>
/// Locates the sondergraph workspace directory that a command operates on.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately mirrors git's discovery: walk up from the current directory looking for
/// the marker, and honour an environment override. See DESIGN.md §5.
/// </para>
/// <para>
/// <b>Todo (task 0002):</b> this is the discovery half only. Locking, atomic writes and
/// stale-lock handling are that task's job. Nothing here acquires a lock yet, so this
/// type is currently safe to call but not yet safe to write through.
/// </para>
/// </remarks>
public static class WorkspaceLocator
{
    /// <summary>Environment variable that overrides discovery, like <c>GIT_DIR</c>.</summary>
    public const string EnvironmentVariable = "SONDERGRAPH_WORKSPACE";

    /// <summary>Directory name marking a workspace root.</summary>
    public const string MarkerDirectory = ".sondergraph";

    /// <summary>
    /// Finds the workspace root containing <paramref name="startDirectory"/>, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    /// <param name="startDirectory">Directory to begin the upward search from.</param>
    /// <returns>Absolute path to the workspace root, or <see langword="null"/>.</returns>
    public static string? Find(string? startDirectory = null)
    {
        var overridden = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            var resolved = Path.GetFullPath(overridden);
            return Directory.Exists(resolved) ? resolved : null;
        }

        var current = new DirectoryInfo(
            Path.GetFullPath(startDirectory ?? Directory.GetCurrentDirectory()));

        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, MarkerDirectory)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
