using Sondergraph.Core;

namespace Sondergraph.Cli;

/// <summary>
/// Exit codes. Distinct values so scripts can branch without parsing stderr.
/// </summary>
/// <remarks>
/// These are part of the CLI's contract. The mapping from <see cref="ErrorCode"/> is
/// centralised in <see cref="MapExitCode"/> so the API can mirror it in HTTP statuses
/// without the two drifting.
/// </remarks>
internal static class ExitCode
{
    internal const int Success = 0;

    /// <summary>Generic failure with no more specific code.</summary>
    internal const int Failure = 1;

    /// <summary>Bad usage: unknown command, missing or invalid argument.</summary>
    internal const int Usage = 2;

    /// <summary>No workspace found, or the workspace is invalid.</summary>
    internal const int Workspace = 3;

    /// <summary>The workspace is locked by another process.</summary>
    internal const int Locked = 4;

    /// <summary>Waiting for the lock timed out.</summary>
    internal const int LockTimeout = 5;

    /// <summary>Maps a Core error code to a process exit code.</summary>
    internal static int MapExitCode(ErrorCode code) => code switch
    {
        ErrorCode.Unknown => Success,
        ErrorCode.WorkspaceNotFound or ErrorCode.WorkspaceInvalid => Workspace,
        ErrorCode.WorkspaceLocked or ErrorCode.WorkspaceLockStale => Locked,
        ErrorCode.WorkspaceLockTimeout => LockTimeout,
        ErrorCode.PathOutsideWorkspace => Usage,
        ErrorCode.ConcurrentModification => Failure,
        _ => Failure,
    };
}
