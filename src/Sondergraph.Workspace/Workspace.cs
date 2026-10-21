using Sondergraph.Core;

namespace Sondergraph.Workspaces;

/// <summary>
/// A located sondergraph workspace: a directory containing user data.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on git's repository discovery (DESIGN.md §5). Obtained via
/// <see cref="Locate"/> or <see cref="Create"/>; constructing one directly does not
/// validate or lock anything.
/// </para>
/// <para>
/// This type is about <em>where</em> the data lives. Locking and writing are
/// <see cref="WorkspaceLock"/> and <see cref="AtomicFile"/> respectively.
/// </para>
/// </remarks>
public sealed class Workspace
{
    /// <summary>Environment variable that overrides discovery, like <c>GIT_DIR</c>.</summary>
    public const string EnvironmentVariable = "SONDERGRAPH_WORKSPACE";

    /// <summary>Directory holding app-internal state. Marks a workspace root.</summary>
    public const string StateDirectoryName = ".sondergraph";

    /// <summary>Directory holding derived, disposable data.</summary>
    public const string CacheDirectoryName = "cache";

    /// <summary>Name of the lock file within the state directory.</summary>
    public const string LockFileName = "lock";

    private Workspace(string root)
    {
        Root = root;
    }

    /// <summary>Absolute path to the workspace root.</summary>
    public string Root { get; }

    /// <summary>Absolute path to <c>data/</c>, the user's data directory.</summary>
    public string DataDirectory => Path.Combine(Root, "data");

    /// <summary>Absolute path to the app state directory.</summary>
    public string StateDirectory => Path.Combine(DataDirectory, StateDirectoryName);

    /// <summary>Absolute path to the disposable cache directory.</summary>
    public string CacheDirectory => Path.Combine(StateDirectory, CacheDirectoryName);

    /// <summary>Absolute path to the lock file.</summary>
    public string LockFilePath => Path.Combine(StateDirectory, LockFileName);

    /// <summary>Absolute path to <c>people/</c>.</summary>
    public string PeopleDirectory => Path.Combine(DataDirectory, "people");

    /// <summary>Absolute path to <c>raw/</c>, the append-only ingest dumps.</summary>
    public string RawDirectory => Path.Combine(DataDirectory, "raw");

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for a workspace root,
    /// honouring <see cref="EnvironmentVariable"/>.
    /// </summary>
    /// <param name="startDirectory">Where to begin. Defaults to the current directory.</param>
    /// <returns>The workspace, or a failure carrying a hint.</returns>
    public static Result<Workspace> Locate(string? startDirectory = null)
    {
        // An explicit argument wins over the environment variable.
        //
        // It previously did not: the override was honoured even when a caller passed a path,
        // so Workspace.Locate("/somewhere/else") quietly returned the workspace named by
        // SONDERGRAPH_WORKSPACE. That made the web UI's "switch data directory" report
        // success while changing nothing, and it is the same trap for any other caller.
        var overridden = string.IsNullOrWhiteSpace(startDirectory)
            ? Environment.GetEnvironmentVariable(EnvironmentVariable)
            : null;

        if (!string.IsNullOrWhiteSpace(overridden))
        {
            var resolved = Path.GetFullPath(overridden);

            if (!Directory.Exists(resolved))
            {
                return Result.Fail<Workspace>(
                    ErrorCode.WorkspaceNotFound,
                    $"{EnvironmentVariable} points at '{resolved}', which does not exist.",
                    $"Unset {EnvironmentVariable} or point it at a real directory.");
            }

            // An explicit override is honoured even when the marker is absent, so that
            // `sg init` and recovery workflows work in a not-yet-initialised directory.
            // The caller decides whether an initialised workspace is required.
            return Result.Ok(new Workspace(resolved));
        }

        var start = startDirectory ?? Directory.GetCurrentDirectory();
        DirectoryInfo? current;

        try
        {
            current = new DirectoryInfo(Path.GetFullPath(start));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result.Fail<Workspace>(
                ErrorCode.WorkspaceInvalid,
                $"'{start}' is not a usable directory path.",
                "Run this command from a directory path.");
        }

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "data", StateDirectoryName);

            if (Directory.Exists(candidate))
            {
                return Result.Ok(new Workspace(current.FullName));
            }

            current = current.Parent;
        }

        return Result.Fail<Workspace>(
            ErrorCode.WorkspaceNotFound,
            "Not inside a sondergraph workspace (no 'data/.sondergraph' found in this or any parent directory).",
            $"Run 'sg init' here, or set {EnvironmentVariable}.");
    }

    /// <summary>
    /// Creates the workspace directory structure at <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Idempotent: creating an already-initialised workspace succeeds without disturbing
    /// existing data. It never deletes or overwrites.
    /// </remarks>
    /// <param name="root">Directory to initialise as a workspace root.</param>
    public static Result<Workspace> Create(string root)
    {
        var workspace = new Workspace(Path.GetFullPath(root));

        // Ordered so a failure part-way leaves a coherent (if incomplete) tree rather
        // than a workspace rooted in a directory that does not exist.
        string[] directories =
        [
            workspace.DataDirectory,
            workspace.StateDirectory,
            workspace.CacheDirectory,
            workspace.PeopleDirectory,
            workspace.RawDirectory,
        ];

        foreach (var directory in directories)
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Fail<Workspace>(
                    ErrorCode.WorkspaceInvalid,
                    $"Could not create '{directory}': {ex.Message}",
                    "Check permissions on the parent directory.");
            }
        }

        return Result.Ok(workspace);
    }

    /// <summary>
    /// Checks that this workspace is structurally usable.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Locate"/> because an explicit
    /// <see cref="EnvironmentVariable"/> override can point at an uninitialised directory,
    /// and callers that need a real workspace must be able to say so.
    /// </remarks>
    public Result Validate()
    {
        if (!Directory.Exists(DataDirectory))
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"'{Root}' has no 'data' directory.",
                "Run 'sg init' to initialise a workspace here.");
        }

        if (!Directory.Exists(StateDirectory))
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"'{DataDirectory}' has no '{StateDirectoryName}' directory.",
                "Run 'sg init' to initialise a workspace here.");
        }

        return Result.Ok();
    }

    /// <summary>
    /// Resolves a workspace-relative path to an absolute one, refusing to escape the root.
    /// </summary>
    /// <remarks>
    /// The containment check is the point: callers pass paths derived from user data
    /// (a person id, a platform name), and a value like <c>../../etc</c> must not resolve.
    /// </remarks>
    /// <param name="relativePath">Path relative to the workspace root.</param>
    public Result<string> ResolvePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var combined = Path.GetFullPath(Path.Combine(Root, relativePath));

        // Compare with a separator appended so that '/w/ws-evil' does not pass as being
        // inside '/w/ws'. Ordinal comparison: this is a structural check, not a culture one.
        var rootWithSeparator = Root.EndsWith(Path.DirectorySeparatorChar)
            ? Root
            : Root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.Ordinal)
            && !string.Equals(combined, Root, StringComparison.Ordinal))
        {
            return Result.Fail<string>(
                ErrorCode.PathOutsideWorkspace,
                $"Path '{relativePath}' resolves outside the workspace.",
                "Paths must stay within the workspace root.");
        }

        return Result.Ok(combined);
    }

    /// <inheritdoc />
    public override string ToString() => Root;
}
