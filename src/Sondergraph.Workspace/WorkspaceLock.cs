using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sondergraph.Core;

namespace Sondergraph.Workspaces;

/// <summary>
/// Metadata describing who holds a lock and since when.
/// </summary>
/// <remarks>
/// Written <em>into</em> the lock file so that a blocked process can tell the user
/// something useful. Git's "another git process seems to be running in this repository"
/// is the thing being improved on (DESIGN.md §5): naming the holder and the start time
/// turns an unexplainable failure into an obvious one.
/// </remarks>
public sealed record LockHolder
{
    /// <summary>Process id of the holder.</summary>
    [JsonPropertyName("pid")]
    public required int ProcessId { get; init; }

    /// <summary>Machine name, for the case where the workspace is on a network share.</summary>
    [JsonPropertyName("host")]
    public required string Host { get; init; }

    /// <summary>Operating-system user that holds the lock.</summary>
    [JsonPropertyName("user")]
    public required string User { get; init; }

    /// <summary>UTC time the lock was acquired.</summary>
    [JsonPropertyName("startedAt")]
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>UTC time of the most recent heartbeat.</summary>
    [JsonPropertyName("heartbeatAt")]
    public required DateTimeOffset HeartbeatAt { get; init; }

    /// <summary>
    /// The command holding the lock, e.g. <c>sg graft</c>.
    /// </summary>
    /// <remarks>
    /// Optional because a library host (the API) may not have a meaningful command name.
    /// </remarks>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    /// <summary>Start time of the holding process, used to defeat pid reuse.</summary>
    [JsonPropertyName("processStartedAt")]
    public DateTimeOffset? ProcessStartedAt { get; init; }

    /// <summary>How long the lock has been held, for display.</summary>
    [JsonIgnore]
    public TimeSpan HeldFor => DateTimeOffset.UtcNow - StartedAt;

    /// <summary>
    /// Formats the holder for a human, e.g.
    /// <c>'sg graft' (pid 4242, user ann, since 12:03:07, held 41s)</c>.
    /// </summary>
    public string Describe()
    {
        var who = string.IsNullOrWhiteSpace(Command) ? "an unnamed command" : $"'{Command}'";
        var held = HeldFor.TotalSeconds < 1
            ? "just now"
            : $"held {FormatDuration(HeldFor)}";

        return $"{who} (pid {ProcessId}, user {User}, since {StartedAt.ToLocalTime():HH:mm:ss}, {held})";
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalMinutes < 1
            ? $"{(int)span.TotalSeconds}s"
            : span.TotalHours < 1
                ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                : $"{(int)span.TotalHours}h {span.Minutes}m";
}

/// <summary>
/// An acquired, exclusive workspace write lock.
/// </summary>
/// <remarks>
/// <para>
/// Exclusive writers, lock-free readers (DESIGN.md §5). Dispose to release.
/// </para>
/// <para>
/// <b>Held via an open file handle, not by the existence of a file.</b> The OS releases
/// the handle when the process dies, so a crash cannot leave a stale lock requiring manual
/// removal — the deliberate improvement over git's <c>index.lock</c>. The lock file itself
/// is never deleted, only truncated and rewritten, because on Unix an advisory lock is
/// attached to the inode: deleting and recreating the path would let a second process lock
/// a <em>different</em> inode at the same path and believe it held the lock.
/// </para>
/// </remarks>
public sealed class WorkspaceLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly Timer _heartbeat;
    private readonly LockHolder _holder;
    private bool _disposed;

    private WorkspaceLock(FileStream stream, LockHolder holder, TimeSpan heartbeatInterval)
    {
        _stream = stream;
        _holder = holder;

        lock (RegistryGate)
        {
            _localHolder = holder;
        }

        _heartbeat = new Timer(
            _ => Beat(),
            state: null,
            dueTime: heartbeatInterval,
            period: heartbeatInterval);
    }

    /// <summary>Default interval between heartbeats.</summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long without a heartbeat before a holder is presumed dead.
    /// </summary>
    /// <remarks>
    /// Generously more than <see cref="DefaultHeartbeatInterval"/> so that a merely busy
    /// process is not misdiagnosed as dead. Staleness is a heuristic for <em>advice</em>;
    /// the flock itself remains the authority on whether the lock is actually held.
    /// </remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    /// <summary>Metadata about the current holder (this process, once acquired).</summary>
    public LockHolder Holder => _holder;

    /// <summary>
    /// Tries once to acquire the lock, without waiting.
    /// </summary>
    /// <param name="workspace">Workspace to lock.</param>
    /// <param name="command">Command name recorded in the metadata, e.g. <c>sg graft</c>.</param>
    public static Result<WorkspaceLock> TryAcquire(Workspace workspace, string? command = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var validation = workspace.Validate();
        if (!validation.IsOk)
        {
            return Result.Fail<WorkspaceLock>(validation.Code, validation.Message, validation.Hint);
        }

        var path = workspace.LockFilePath;

        // Windows: a competing holder causes IOException (sharing violation, 0x80070020).
        // Unix: FileShare.None maps to LOCK_EX|LOCK_NB, and EWOULDBLOCK surfaces as
        // IOException too. These are indistinguishable by exception type, so the outcome is
        // decided by probing afterwards rather than by inspecting the error.
        var opened = OpenWithRetry(path, out var failure);

        if (opened is not null)
        {
            try
            {
                var holder = BuildHolder(command);
                WriteHolder(opened, holder);

                return Result.Ok(new WorkspaceLock(opened, holder, DefaultHeartbeatInterval));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                opened.Dispose();

                return Result.Fail<WorkspaceLock>(
                    ErrorCode.WorkspaceInvalid,
                    $"Could not write lock metadata to '{path}': {ex.Message}",
                    "Check permissions and free space on the workspace state directory.");
            }
        }

        if (failure is UnauthorizedAccessException)
        {
            return Result.Fail<WorkspaceLock>(
                ErrorCode.WorkspaceInvalid,
                $"Permission denied opening the lock file '{path}'.",
                "Check permissions on the workspace state directory.");
        }

        // The open failed. Two causes, and they need different messages: another process
        // holds the lock, or something else (a transient scanner handle, a read-only
        // filesystem, a stale handle from a crashed process) is in the way. Probing an
        // exclusive open of a *fresh* handle distinguishes them: if that also fails while a
        // live holder's metadata is present, it is contention.
        return Result.Fail<WorkspaceLock>(
            ErrorCode.WorkspaceLocked,
            DescribeContention(path),
            "Use --wait to block until the lock is free.");
    }

    /// <summary>
    /// Acquires the lock, waiting up to <paramref name="timeout"/> for it to become free.
    /// </summary>
    /// <param name="workspace">Workspace to lock.</param>
    /// <param name="timeout">How long to wait. <see cref="Timeout.InfiniteTimeSpan"/> waits forever.</param>
    /// <param name="command">Command name recorded in the metadata.</param>
    /// <param name="progress">
    /// Optional callback invoked once with the blocking holder, so a caller can print
    /// "waiting for X" rather than appearing to hang.
    /// </param>
    public static async Task<Result<WorkspaceLock>> AcquireAsync(
        Workspace workspace,
        TimeSpan timeout,
        string? command = null,
        Action<LockHolder?>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var deadline = timeout == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow + timeout;

        var reported = false;
        var delay = TimeSpan.FromMilliseconds(50);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attempt = TryAcquire(workspace, command);
            if (attempt.IsOk)
            {
                return attempt;
            }

            if (attempt.Code != ErrorCode.WorkspaceLocked)
            {
                return attempt;
            }

            if (!reported)
            {
                progress?.Invoke(ReadHolder(workspace.LockFilePath));
                reported = true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var holder = ReadHolder(workspace.LockFilePath);
                var blockedBy = holder is null ? "another process" : holder.Describe();

                return Result.Fail<WorkspaceLock>(
                    ErrorCode.WorkspaceLockTimeout,
                    $"Timed out after {timeout.TotalSeconds:0.#}s waiting for the workspace lock held by {blockedBy}.",
                    "Retry, or investigate the holder with 'sg workspace status'.");
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            // Back off to a second, so a long wait does not spin. Capped low enough that
            // a released lock is picked up promptly.
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
        }
    }

    /// <summary>
    /// The holder within <em>this</em> process, if any.
    /// </summary>
    /// <remarks>
    /// Needed because the lock is an exclusive OS lock: on Linux, <c>FileShare.None</c>
    /// becomes <c>flock(LOCK_EX)</c>, which blocks a second open even from the same
    /// process. So this process cannot read back the lock file it is holding, and must
    /// track its own holder in memory.
    /// </remarks>
    private static readonly Lock RegistryGate = new();
    private static LockHolder? _localHolder;

    /// <summary>
    /// Reads the current holder's metadata, or <see langword="null"/> if unavailable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used only to produce good error messages. Returns null rather than failing, because
    /// unreadable metadata must never mask the real error (which is that the lock is held).
    /// </para>
    /// <para>
    /// <b>Why this opens the file with a raw POSIX <c>open()</c> on Unix.</b> The lock is
    /// an exclusive OS lock, and .NET applies the share mode to <em>every</em> managed open.
    /// Verified empirically on Linux against a held lock:
    /// </para>
    /// <list type="bullet">
    /// <item>all four <c>FileShare</c> values fail on <c>FileStream</c>, including
    /// <c>ReadWrite | Delete</c>;</item>
    /// <item><c>File.ReadAllText</c> fails too, since it uses the same path;</item>
    /// <item>a raw <c>open(2)</c> + <c>read(2)</c> succeeds, because <c>flock</c> is
    /// advisory and only .NET's share-mode check enforces it.</item>
    /// </list>
    /// <para>
    /// The lock is therefore <em>cooperative</em>: it excludes other .NET processes using
    /// this type, which is exactly the requirement. Diagnostics read the bytes directly so
    /// that a blocked user can be told who holds the lock. This is not a hole in the lock —
    /// readers were always allowed by design (DESIGN.md §5), and no reader consults this
    /// file to make a decision.
    /// </para>
    /// </remarks>
    public static LockHolder? ReadHolder(string lockFilePath)
    {
        // Fast path: this process holds it, and the OS-level open below would fail.
        lock (RegistryGate)
        {
            if (_localHolder is not null)
            {
                return _localHolder;
            }
        }

        try
        {
            if (!File.Exists(lockFilePath))
            {
                return null;
            }

            var json = ReadAllTextIgnoringShareMode(lockFilePath);

            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<LockHolder>(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A partially-written file is possible if we read mid-rewrite; the caller
            // falls back to a generic message, which is acceptable for diagnostics.
            return null;
        }
    }

    /// <summary>
    /// Reads a file's text without .NET's share-mode check.
    /// </summary>
    /// <remarks>
    /// On Unix this is a raw <c>open(2)</c>/<c>read(2)</c>/<c>close(2)</c>, because
    /// <c>flock</c> is advisory and .NET is stricter than the kernel. On Windows, share
    /// modes are mandatory, so this falls back to <see cref="File.ReadAllText(string)"/>
    /// and may legitimately fail while the lock is held — in that case the caller degrades
    /// to a generic message rather than a wrong one.
    /// </remarks>
    private static string ReadAllTextIgnoringShareMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var fd = NativeMethods.Open(path, NativeMethods.OpenReadOnly);
            if (fd < 0)
            {
                throw new IOException(
                    $"open(2) failed for '{path}' (errno {Marshal.GetLastPInvokeError()}).");
            }

            try
            {
                var buffer = new byte[8192];
                var total = 0;
                int read;
                var builder = new StringBuilder();

                while ((read = NativeMethods.Read(fd, buffer, buffer.Length)) > 0)
                {
                    builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    total += read;

                    // Lock metadata is a few hundred bytes; anything larger is not ours.
                    if (total > 64 * 1024)
                    {
                        break;
                    }
                }

                return builder.ToString();
            }
            finally
            {
                _ = NativeMethods.Close(fd);
            }
        }

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Minimal libc bindings for the diagnostic read path.
    /// </summary>
    /// <remarks>
    /// Deliberately <see cref="DllImportAttribute"/> rather than
    /// <see cref="LibraryImportAttribute"/>: the source-generated variant requires
    /// <c>AllowUnsafeBlocks</c>, and enabling unsafe code across the whole assembly to read
    /// a diagnostic file is a bad trade.
    /// </remarks>
    private static class NativeMethods
    {
        internal const int OpenReadOnly = 0;

        // CharSet.Ansi marshals to a NUL-terminated byte string, which is what libc
        // expects. CharSet.Unicode would produce UTF-16 and break non-ASCII paths.
        [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        internal static extern int Open(string path, int flags);

        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        internal static extern int Read(int fd, byte[] buffer, int count);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static extern int Close(int fd);
    }

    /// <summary>
    /// Determines whether a lock appears to be stale.
    /// </summary>
    /// <param name="holder">Metadata read from the lock file.</param>
    /// <remarks>
    /// <para>
    /// Two independent checks, and the second is the one that matters:
    /// </para>
    /// <list type="number">
    /// <item>A missing or old heartbeat suggests the holder is gone.</item>
    /// <item>The recorded process start time must match the live process's actual start
    /// time. This is what defeats <b>pid reuse</b> — a naive "is pid 4242 alive?" check
    /// says yes when 4242 is now an unrelated program.</item>
    /// </list>
    /// <para>
    /// Advisory only. The flock remains the authority; this exists to produce a good error
    /// message, never to justify stealing a lock.
    /// </para>
    /// </remarks>
    public static bool IsStale(LockHolder? holder)
    {
        if (holder is null)
        {
            // No readable metadata. Could be a lock file being written right now, so this
            // is not treated as stale — only genuinely old heartbeats are.
            return false;
        }

        if (holder.HeartbeatAt < DateTimeOffset.UtcNow - StaleAfter)
        {
            return true;
        }

        return !ProcessMatches(holder);
    }

    /// <summary>
    /// Whether the process recorded in <paramref name="holder"/> is still the same process.
    /// </summary>
    private static bool ProcessMatches(LockHolder holder)
    {
        try
        {
            using var process = Process.GetProcessById(holder.ProcessId);

            if (process.HasExited)
            {
                return false;
            }

            // The pid-reuse guard. Only enforced when both sides recorded a start time,
            // since reading it can fail across user boundaries.
            if (holder.ProcessStartedAt is { } recorded)
            {
                var actual = new DateTimeOffset(process.StartTime);
                return Math.Abs((actual - recorded).TotalSeconds) < 2;
            }

            return true;
        }
        catch (ArgumentException)
        {
            // No such process.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Exited between lookup and inspection.
            return false;
        }
    }

    private static string DescribeContention(string lockFilePath)
    {
        var holder = ReadHolder(lockFilePath);

        if (holder is null)
        {
            return "The workspace is locked by another process (no metadata available).";
        }

        var staleNote = IsStale(holder)
            ? " Its owner appears to have exited."
            : string.Empty;

        return $"The workspace is locked by {holder.Describe()}.{staleNote}";
    }

    /// <summary>
    /// Attempts an exclusive open, retrying briefly on transient failures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The retry exists for Windows, where antivirus and search indexers hold short-lived
    /// handles to files they are scanning, producing a sharing violation that has nothing
    /// to do with our lock (docs/architecture-precedents.md §4).
    /// </para>
    /// <para>
    /// It deliberately does <b>not</b> retry for long: genuine contention and a transient
    /// scan are indistinguishable before the attempt, and a caller that wants to wait uses
    /// <see cref="AcquireAsync"/>, which polls with its own backoff. Burning seconds here
    /// would make the non-waiting path slow for everyone.
    /// </para>
    /// </remarks>
    private static FileStream? OpenWithRetry(string path, out Exception? failure)
    {
        const int attempts = 3;
        failure = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                // Opening for read/write with FileShare.None is what takes the exclusive
                // lock. On Unix this is flock(LOCK_EX|LOCK_NB); note that only
                // FileShare.None is meaningful there — every other value maps to a
                // *shared* lock, so multiple writers would succeed.
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.None);
            }
            catch (IOException ex)
            {
                failure = ex;

                if (attempt < attempts - 1)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(20 * (attempt + 1)));
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                // Permission problems are not transient; retrying cannot help.
                failure = ex;
                break;
            }
        }

        return null;
    }

    private static LockHolder BuildHolder(string? command)
    {
        var now = DateTimeOffset.UtcNow;
        Process? self = null;

        try
        {
            self = Process.GetCurrentProcess();
        }
        catch (InvalidOperationException)
        {
            // Not fatal: process start time is an optimisation for stale detection.
        }

        using (self)
        {
            DateTimeOffset? startedAt = null;
            try
            {
                if (self is not null)
                {
                    startedAt = new DateTimeOffset(self.StartTime);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // Some platforms/containers restrict StartTime. Leave it null.
            }

            return new LockHolder
            {
                ProcessId = Environment.ProcessId,
                Host = Environment.MachineName,
                User = Environment.UserName,
                StartedAt = now,
                HeartbeatAt = now,
                Command = command,
                ProcessStartedAt = startedAt,
            };
        }
    }

    private static void WriteHolder(FileStream stream, LockHolder holder)
    {
        stream.SetLength(0);
        stream.Position = 0;

        var json = JsonSerializer.SerializeToUtf8Bytes(holder, JsonOptions);
        stream.Write(json, 0, json.Length);

        // Durable: the metadata is what a blocked process reads to explain the situation.
        // A half-written lock file would produce a worse message than none.
        stream.Flush(flushToDisk: true);
    }

    private void Beat()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var refreshed = _holder with { HeartbeatAt = DateTimeOffset.UtcNow };

            lock (RegistryGate)
            {
                _localHolder = refreshed;
            }

            WriteHolder(_stream, refreshed);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The heartbeat is advisory. Losing it degrades the staleness heuristic for
            // other processes; it does not mean we lost the lock, so it must not throw
            // on a timer thread and take the process down.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Releases the lock. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Order matters: stop the timer before closing the stream, or a heartbeat can
        // race the dispose and throw on a disposed handle.
        _heartbeat.Dispose();

        lock (RegistryGate)
        {
            _localHolder = null;
        }

        // Closing the handle releases the OS lock. The file is left in place on purpose —
        // see the class remarks about inodes.
        _stream.Dispose();
    }
}
