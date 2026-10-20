using Sondergraph.Core;

namespace Sondergraph.Workspaces.Tests;

public sealed class WorkspaceLockTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly Workspace _workspace;

    public WorkspaceLockTests()
    {
        var created = Workspace.Create(_tree.CreateDirectory("ws"));
        Assert.True(created.IsOk, created.Message);
        _workspace = created.ValueOrThrow();
    }

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Acquires_when_uncontended()
    {
        using var acquired = WorkspaceLock.TryAcquire(_workspace, "sg test");

        Assert.True(acquired.IsOk, acquired.Message);
        Assert.Equal("sg test", acquired.ValueOrThrow().Holder.Command);
    }

    [Fact]
    public void Second_acquisition_in_same_process_fails()
    {
        // The lock must exclude within a process too: two hosts in one process (a test
        // runner, or a future in-process desktop shell) must not both believe they hold it.
        using var first = WorkspaceLock.TryAcquire(_workspace, "first");
        Assert.True(first.IsOk, first.Message);

        var second = WorkspaceLock.TryAcquire(_workspace, "second");

        Assert.False(second.IsOk);
        Assert.Equal(ErrorCode.WorkspaceLocked, second.Code);
    }

    [Fact]
    public void Contention_message_names_the_holder()
    {
        // The whole point of the metadata: a blocked user learns who to wait for.
        using var first = WorkspaceLock.TryAcquire(_workspace, "sg graft");
        Assert.True(first.IsOk, first.Message);

        var second = WorkspaceLock.TryAcquire(_workspace, "sg publish");

        Assert.False(second.IsOk);
        Assert.Contains("sg graft", second.Message);
        Assert.Contains("pid", second.Message);
        Assert.Contains(Environment.UserName, second.Message);
    }

    [Fact]
    public void Lock_is_released_on_dispose()
    {
        var first = WorkspaceLock.TryAcquire(_workspace, "first");
        Assert.True(first.IsOk, first.Message);
        first.ValueOrThrow().Dispose();

        var second = WorkspaceLock.TryAcquire(_workspace, "second");

        Assert.True(second.IsOk, second.Message);
        second.ValueOrThrow().Dispose();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var acquired = WorkspaceLock.TryAcquire(_workspace);
        var @lock = acquired.ValueOrThrow();

        @lock.Dispose();
        @lock.Dispose();

        // Still acquirable, i.e. double-dispose did not corrupt state.
        var again = WorkspaceLock.TryAcquire(_workspace);
        Assert.True(again.IsOk, again.Message);
        again.ValueOrThrow().Dispose();
    }

    [Fact]
    public async Task Acquire_waits_until_the_lock_is_released()
    {
        using var first = WorkspaceLock.TryAcquire(_workspace, "holder");
        Assert.True(first.IsOk, first.Message);

        // Release after a delay; the waiter must succeed rather than time out.
        var releaser = Task.Run(async () =>
        {
            await Task.Delay(300);
            first.ValueOrThrow().Dispose();
        });

        var acquired = await WorkspaceLock.AcquireAsync(
            _workspace,
            TimeSpan.FromSeconds(10),
            "waiter");

        await releaser;

        Assert.True(acquired.IsOk, acquired.Message);
        acquired.ValueOrThrow().Dispose();
    }

    [Fact]
    public async Task Acquire_times_out_and_reports_the_holder()
    {
        using var first = WorkspaceLock.TryAcquire(_workspace, "sg long-running");
        Assert.True(first.IsOk, first.Message);

        var acquired = await WorkspaceLock.AcquireAsync(
            _workspace,
            TimeSpan.FromMilliseconds(300),
            "waiter");

        Assert.False(acquired.IsOk);
        Assert.Equal(ErrorCode.WorkspaceLockTimeout, acquired.Code);
        // A timeout message that does not say who blocked you is a support ticket.
        Assert.Contains("sg long-running", acquired.Message);
    }

    [Fact]
    public async Task Acquire_reports_progress_to_the_caller()
    {
        // A caller must be able to print "waiting for X" instead of appearing to hang.
        using var first = WorkspaceLock.TryAcquire(_workspace, "sg heavy");
        Assert.True(first.IsOk, first.Message);

        LockHolder? reported = null;

        var acquired = await WorkspaceLock.AcquireAsync(
            _workspace,
            TimeSpan.FromMilliseconds(200),
            "waiter",
            holder => reported = holder);

        Assert.False(acquired.IsOk);
        Assert.NotNull(reported);
        Assert.Equal("sg heavy", reported.Command);
    }

    [Fact]
    public void Lock_file_survives_release()
    {
        // Deleted-and-recreated lock files would let a second process lock a different
        // inode at the same path and wrongly believe it held the lock. The file must stay.
        var acquired = WorkspaceLock.TryAcquire(_workspace);
        acquired.ValueOrThrow().Dispose();

        Assert.True(File.Exists(_workspace.LockFilePath));
    }

    [Fact]
    public void Holder_metadata_is_readable_while_locked()
    {
        using var acquired = WorkspaceLock.TryAcquire(_workspace, "sg inspect");
        Assert.True(acquired.IsOk, acquired.Message);

        var holder = WorkspaceLock.ReadHolder(_workspace.LockFilePath);

        Assert.NotNull(holder);
        Assert.Equal(Environment.ProcessId, holder.ProcessId);
        Assert.Equal("sg inspect", holder.Command);
    }

    [Fact]
    public void Held_lock_is_not_reported_stale()
    {
        using var acquired = WorkspaceLock.TryAcquire(_workspace);
        Assert.True(acquired.IsOk, acquired.Message);

        var holder = WorkspaceLock.ReadHolder(_workspace.LockFilePath);

        Assert.False(WorkspaceLock.IsStale(holder));
    }

    [Fact]
    public void Dead_holder_is_reported_stale()
    {
        // Simulates the crash case: metadata naming a pid that no longer exists.
        var ghost = new LockHolder
        {
            ProcessId = int.MaxValue - 1,
            Host = "ghost",
            User = "nobody",
            StartedAt = DateTimeOffset.UtcNow,
            HeartbeatAt = DateTimeOffset.UtcNow,
            Command = "sg crashed",
        };

        Assert.True(WorkspaceLock.IsStale(ghost));
    }

    [Fact]
    public void Old_heartbeat_is_reported_stale()
    {
        var idle = new LockHolder
        {
            ProcessId = Environment.ProcessId,
            Host = Environment.MachineName,
            User = Environment.UserName,
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
            // Still a live pid, but the heartbeat stopped long ago.
            HeartbeatAt = DateTimeOffset.UtcNow - WorkspaceLock.StaleAfter - TimeSpan.FromSeconds(5),
        };

        Assert.True(WorkspaceLock.IsStale(idle));
    }

    [Fact]
    public void Pid_reuse_is_detected_by_process_start_time()
    {
        // A live pid whose recorded start time does not match is a *different* process
        // that inherited the number. This is the check a naive liveness test misses.
        var reused = new LockHolder
        {
            ProcessId = Environment.ProcessId,
            Host = Environment.MachineName,
            User = Environment.UserName,
            StartedAt = DateTimeOffset.UtcNow,
            HeartbeatAt = DateTimeOffset.UtcNow,
            ProcessStartedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(30),
        };

        Assert.True(WorkspaceLock.IsStale(reused));
    }

    [Fact]
    public void Null_holder_is_not_called_stale()
    {
        // Unreadable metadata can mean "being written right now". Declaring it stale would
        // invite stealing a lock that is genuinely held.
        Assert.False(WorkspaceLock.IsStale(null));
    }

    [Fact]
    public void Describe_formats_a_useful_message()
    {
        var holder = new LockHolder
        {
            ProcessId = 4242,
            Host = "host",
            User = "ann",
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(41),
            HeartbeatAt = DateTimeOffset.UtcNow,
            Command = "sg graft",
        };

        var described = holder.Describe();

        Assert.Contains("sg graft", described);
        Assert.Contains("4242", described);
        Assert.Contains("ann", described);
        Assert.Contains("41s", described);
    }

    [Fact]
    public void Unnamed_command_still_describes_cleanly()
    {
        var holder = new LockHolder
        {
            ProcessId = 1,
            Host = "host",
            User = "svc",
            StartedAt = DateTimeOffset.UtcNow,
            HeartbeatAt = DateTimeOffset.UtcNow,
            Command = null,
        };

        Assert.Contains("an unnamed command", holder.Describe());
    }
}
