using Sondergraph.Core;
using Sondergraph.Workspaces;

namespace Sondergraph.Cli;

/// <summary>
/// Implements <c>sg init</c> and <c>sg workspace ...</c>.
/// </summary>
/// <remarks>
/// Thin: argument handling plus rendering. The interesting behaviour — discovery, locking,
/// staleness — lives in <c>Sondergraph.Workspaces</c> and is shared with the API.
/// </remarks>
internal static class WorkspaceCommands
{
    /// <summary>
    /// Extracts an optional path argument, which is always the last positional argument.
    /// </summary>
    /// <remarks>
    /// Callers pass the raw <c>args</c> array, so the path is not at a fixed index:
    /// <c>sg init [path]</c> vs <c>sg workspace status [path]</c>. Taking the last argument
    /// and ignoring it when it looks like a flag keeps the parsing honest without pulling in
    /// a full argument parser yet.
    /// </remarks>
    private static string? PathArgument(string[] args, int minimumArgs)
    {
        if (args.Length <= minimumArgs)
        {
            return null;
        }

        var candidate = args[^1];
        return candidate.StartsWith('-') ? null : candidate;
    }

    internal static int Init(string[] args)
    {
        // sg init [path]
        var root = PathArgument(args, minimumArgs: 1) ?? Directory.GetCurrentDirectory();

        var created = Workspace.Create(root);
        if (!created.IsOk)
        {
            ConsoleRenderer.WriteFailure(created);
            return ExitCode.MapExitCode(created.Code);
        }

        var workspace = created.ValueOrThrow();

        Console.WriteLine($"Initialised sondergraph workspace at {workspace.Root}");
        Console.WriteLine($"  data:  {workspace.DataDirectory}");

        return ExitCode.Success;
    }

    internal static int Status(string[] args)
    {
        // sg workspace status [path]
        var located = Workspace.Locate(PathArgument(args, minimumArgs: 2));
        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var workspace = located.ValueOrThrow();

        Console.WriteLine($"Workspace: {workspace.Root}");

        var validation = workspace.Validate();
        if (!validation.IsOk)
        {
            ConsoleRenderer.WriteFailure(validation);
            return ExitCode.MapExitCode(validation.Code);
        }

        // Report the lock by trying to take it, then releasing immediately. This is the
        // only honest way to know: the file's existence means nothing, and its metadata
        // may describe a dead process.
        using var acquired = WorkspaceLock.TryAcquire(workspace, "sg workspace status");

        if (acquired.IsOk)
        {
            Console.WriteLine("Lock:      free");
            return ExitCode.Success;
        }

        if (acquired.Code != ErrorCode.WorkspaceLocked)
        {
            ConsoleRenderer.WriteFailure(acquired);
            return ExitCode.MapExitCode(acquired.Code);
        }

        var holder = WorkspaceLock.ReadHolder(workspace.LockFilePath);

        Console.WriteLine("Lock:      held");
        Console.WriteLine($"  holder:  {holder?.Describe() ?? "unknown (no readable metadata)"}");

        if (holder is not null && WorkspaceLock.IsStale(holder))
        {
            // Advisory, and worded as such: the recorded owner may have exited, and the
            // OS releases the lock when it does, so no manual cleanup is ever needed.
            Console.WriteLine("  note:    the recorded owner appears to have exited;");
            Console.WriteLine("           the lock releases automatically once it does.");
        }

        return ExitCode.Locked;
    }

    /// <summary>
    /// Diagnostics for the lock, so a confused user has somewhere to look.
    /// </summary>
    internal static int LockInfo(string[] args)
    {
        // sg workspace lock [path]
        var located = Workspace.Locate(PathArgument(args, minimumArgs: 2));
        if (!located.IsOk)
        {
            ConsoleRenderer.WriteFailure(located);
            return ExitCode.MapExitCode(located.Code);
        }

        var workspace = located.ValueOrThrow();
        var holder = WorkspaceLock.ReadHolder(workspace.LockFilePath);

        Console.WriteLine($"Lock file: {workspace.LockFilePath}");

        if (holder is null)
        {
            Console.WriteLine("Holder:    none recorded");
            return ExitCode.Success;
        }

        Console.WriteLine($"Holder:    {holder.Describe()}");
        Console.WriteLine($"  pid:     {holder.ProcessId}");
        Console.WriteLine($"  host:    {holder.Host}");
        Console.WriteLine($"  user:    {holder.User}");
        Console.WriteLine($"  started: {holder.StartedAt.ToLocalTime():u}");
        Console.WriteLine($"  beat:    {holder.HeartbeatAt.ToLocalTime():u}");
        Console.WriteLine($"  stale:   {WorkspaceLock.IsStale(holder)}");

        return ExitCode.Success;
    }
}

