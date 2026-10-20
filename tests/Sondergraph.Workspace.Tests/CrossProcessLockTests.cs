using System.Diagnostics;
using System.Reflection;
using System.Text;
using Sondergraph.Core;

namespace Sondergraph.Workspaces.Tests;

/// <summary>
/// Cross-process lock behaviour, exercised with real OS processes.
/// </summary>
/// <remarks>
/// <para>
/// The in-process tests in <see cref="WorkspaceLockTests"/> cannot prove mutual exclusion:
/// a static field would satisfy them just as well as an OS lock. These tests spawn actual
/// child processes, which is the only way to know the flock/share-mode machinery works.
/// </para>
/// <para>
/// This is also the case where the Unix <c>FileShare</c> trap matters most — if a share
/// value other than <c>None</c> were used, two processes would both "acquire" and only a
/// test like this would notice.
/// </para>
/// </remarks>
public sealed class CrossProcessLockTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly Workspace _workspace;
    private static readonly string HostPath = LocateHost();

    public CrossProcessLockTests()
    {
        var created = Workspace.Create(_tree.CreateDirectory("ws"));
        Assert.True(created.IsOk, created.Message);
        _workspace = created.ValueOrThrow();
    }

    public void Dispose() => _tree.Dispose();

    /// <summary>
    /// Finds the compiled test host. Tests run from the test project's output directory;
    /// the host is a sibling project under tools/.
    /// </summary>
    private static string LocateHost()
    {
        var here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

        // Walk up from tests/Sondergraph.Workspace.Tests/bin/<cfg>/<tfm>/ to the repo root.
        var directory = new DirectoryInfo(here);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sondergraph.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Could not locate the repository root from '{here}'. The cross-process tests "
                + "need Sondergraph.slnx as a landmark.");
        }

        var configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif

        var host = Path.Combine(
            directory.FullName,
            "tools",
            "Sondergraph.TestHost",
            "bin",
            configuration,
            "net10.0",
            "Sondergraph.TestHost.dll");

        return host;
    }

    private static Process StartHost(params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        info.ArgumentList.Add(HostPath);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start the test host process.");

        return process;
    }

    /// <summary>
    /// Reads a line, or returns null if the process exits first.
    /// </summary>
    private static string? ReadLineOrNull(Process process, TimeSpan timeout)
    {
        var task = process.StandardOutput.ReadLineAsync();
        return task.Wait(timeout) ? task.Result : null;
    }

    [Fact]
    public async Task Two_child_processes_cannot_hold_the_lock_at_once()
    {
        // The load-bearing test for this task. Both children try to acquire; the second
        // must be refused while the first holds it.
        using var first = StartHost("hold", _workspace.Root, "2000", "sg first");

        try
        {
            var acquired = ReadLineOrNull(first, TimeSpan.FromSeconds(30));
            Assert.Equal("ACQUIRED", acquired);

            // Now, while the first genuinely holds it, a second real process must fail.
            using var second = StartHost("verify", _workspace.Root);
            var secondOut = await second.StandardOutput.ReadToEndAsync();
            await second.WaitForExitAsync();

            Assert.Contains("LOCKED:WorkspaceLocked", secondOut);
            // And it must name the holder, which is the improvement over git's message.
            Assert.Contains("sg first", secondOut);
        }
        finally
        {
            if (!first.HasExited)
            {
                first.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task A_crashed_holder_does_not_leave_a_permanent_lock()
    {
        // The central claim of the design: the OS releases the handle on process death,
        // so no manual lock-file removal is ever needed. Simulated by SIGKILL.
        var child = StartHost("hold", _workspace.Root, "60000", "sg doomed");

        try
        {
            var acquired = ReadLineOrNull(child, TimeSpan.FromSeconds(30));
            Assert.Equal("ACQUIRED", acquired);

            // Kill without letting it release. This is the crash case.
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();

            // The lock must now be acquirable, with no cleanup step.
            var recovered = WorkspaceLock.TryAcquire(_workspace, "sg after-crash");

            Assert.True(
                recovered.IsOk,
                $"A killed holder left the workspace locked: {recovered.Message}");
            recovered.ValueOrThrow().Dispose();
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task Stale_metadata_from_a_dead_process_is_reported_as_stale()
    {
        // After a crash the lock is free, but the file still names the dead pid. The
        // staleness heuristic should surface that rather than reporting a phantom holder.
        var child = StartHost("hold", _workspace.Root, "60000", "sg ghost");
        var childPid = child.Id;

        try
        {
            Assert.Equal("ACQUIRED", ReadLineOrNull(child, TimeSpan.FromSeconds(30)));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();

            var holder = WorkspaceLock.ReadHolder(_workspace.LockFilePath);

            Assert.NotNull(holder);
            Assert.Equal(childPid, holder.ProcessId);
            Assert.True(
                WorkspaceLock.IsStale(holder),
                "Metadata naming a dead process should be reported stale.");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task Waiting_process_acquires_after_the_holder_exits()
    {
        // A --wait style caller must proceed once the holder is gone, not time out.
        var child = StartHost("hold", _workspace.Root, "800", "sg brief");

        try
        {
            Assert.Equal("ACQUIRED", ReadLineOrNull(child, TimeSpan.FromSeconds(30)));

            var acquired = await WorkspaceLock.AcquireAsync(
                _workspace,
                TimeSpan.FromSeconds(20),
                "sg waiter");

            Assert.True(acquired.IsOk, acquired.Message);
            acquired.ValueOrThrow().Dispose();
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task A_reader_never_observes_a_torn_file()
    {
        // Proves the atomic write protocol. The writer hammers the file with new values
        // while the reader continuously reads; every read must be a whole value. A torn
        // read would surface as TORN and fail the assertion.
        var child = StartHost("write", _workspace.Root, "data/people/counter.txt", "value", "400");
        using var reader = StartHost("read-loop", _workspace.Root, "data/people/counter.txt", "value", "5");

        var readerOut = await reader.StandardOutput.ReadToEndAsync();
        await reader.WaitForExitAsync();
        await child.WaitForExitAsync();

        Assert.DoesNotContain("TORN", readerOut);
        Assert.DoesNotContain("READERROR", readerOut);
        // Guards against the test passing because the reader never ran.
        Assert.Contains("CLEAN:", readerOut);

        var readCount = int.Parse(
            readerOut.Split("CLEAN:")[1].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(readCount > 0, "Reader performed no reads; the test proved nothing.");
    }
}
