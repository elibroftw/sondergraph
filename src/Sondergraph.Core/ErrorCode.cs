namespace Sondergraph.Core;

/// <summary>
/// Stable, machine-readable error codes.
/// </summary>
/// <remarks>
/// <para>
/// Codes are part of the public contract: the CLI maps them to exit codes, the API maps
/// them to HTTP status plus a <c>code</c> field, and scripts branch on them. See
/// ADR 0001 — progress and errors are types, not strings, which is what lets two shells
/// render the same failure differently without reimplementing the logic.
/// </para>
/// <para>
/// Values are stable. Renaming one is a breaking change to the wire protocol
/// (<see cref="SondergraphVersion.Protocol"/>).
/// </para>
/// </remarks>
public enum ErrorCode
{
    /// <summary>An unspecified failure.</summary>
    Unknown = 0,

    /// <summary>No workspace could be located from the current directory.</summary>
    WorkspaceNotFound,

    /// <summary>The workspace exists but is structurally invalid.</summary>
    WorkspaceInvalid,

    /// <summary>Another process holds the write lock.</summary>
    WorkspaceLocked,

    /// <summary>A lock was found but its owner is gone.</summary>
    WorkspaceLockStale,

    /// <summary>Waiting for the lock timed out.</summary>
    WorkspaceLockTimeout,

    /// <summary>A path escaped the workspace root.</summary>
    PathOutsideWorkspace,

    /// <summary>A file was modified by someone else since it was read.</summary>
    ConcurrentModification,
}
