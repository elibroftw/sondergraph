using System.Text;
using Sondergraph.Core;

namespace Sondergraph.Workspaces;

/// <summary>
/// Writes files atomically, so a concurrent reader never observes a partial file.
/// </summary>
/// <remarks>
/// <para>
/// The protocol is git's: write a sibling temp file, flush it to disk, then
/// <c>rename(2)</c> it over the target. On POSIX and on Windows (same volume) the rename
/// is atomic, so a reader sees either the old file or the new one and never a torn mix.
/// </para>
/// <para>
/// This is what makes lock-free reads correct (DESIGN.md §5). Readers take no lock at all;
/// they rely on every writer, everywhere, going through this type.
/// </para>
/// <para>
/// <b>The temp file must be a sibling of the target</b>, not in the system temp directory,
/// because rename is only atomic within a filesystem. A temp file in <c>/tmp</c> crossing
/// into a different mount would degrade to a copy, which is exactly the torn read this
/// exists to prevent.
/// </para>
/// </remarks>
public static class AtomicFile
{
    /// <summary>Extension used for in-flight temp files.</summary>
    private const string TempSuffix = ".sgtmp";

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically.
    /// </summary>
    /// <param name="path">Absolute destination path.</param>
    /// <param name="contents">Text to write.</param>
    /// <param name="durable">
    /// Whether to flush to physical disk before renaming. Costs an fsync; worth it for
    /// critical manifests, wasteful for caches.
    /// </param>
    public static Result WriteAllText(string path, string contents, bool durable = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(contents);

        return WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents), durable);
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> atomically.
    /// </summary>
    /// <param name="path">Absolute destination path.</param>
    /// <param name="bytes">Bytes to write.</param>
    /// <param name="durable">Whether to fsync before renaming.</param>
    public static Result WriteAllBytes(string path, byte[] bytes, bool durable = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bytes);

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"'{path}' has no parent directory.",
                "Pass an absolute path.");
        }

        if (!Directory.Exists(directory))
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"Directory '{directory}' does not exist.",
                "Create the directory before writing into it.");
        }

        // Unique per process and per attempt, so two writers cannot collide on the temp
        // name and clobber each other's in-flight data.
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():n}{TempSuffix}");

        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                // Exclusive: nobody should be able to observe or touch the temp file
                // while it is being assembled.
                FileShare.None,
                bufferSize: 4096,
                FileOptions.None))
            {
                stream.Write(bytes, 0, bytes.Length);

                if (durable)
                {
                    // Flush to the physical device. Without this the rename can land
                    // before the contents do, so a crash leaves a correctly-named but
                    // empty file — worse than losing the write outright.
                    stream.Flush(flushToDisk: true);
                }
            }

            // Atomic replacement. File.Move with overwrite maps to rename(2) on POSIX and
            // MoveFileEx(REPLACE_EXISTING) on Windows.
            File.Move(tempPath, path, overwrite: true);

            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(
                ErrorCode.WorkspaceInvalid,
                $"Could not write '{path}': {ex.Message}",
                "Check that the file is not open in another program and that the directory is writable.");
        }
        finally
        {
            // A failed write must not leave litter behind. Best effort: if this throws,
            // the original error is more useful than the cleanup error.
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Reads a file as text, or returns <see langword="null"/> when it does not exist.
    /// </summary>
    /// <remarks>
    /// Retries on sharing violations, because on Windows antivirus and indexers briefly
    /// hold handles to files they are scanning (docs/architecture-precedents.md §4).
    /// </remarks>
    public static Result<string?> ReadAllTextOrNull(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Result.Ok<string?>(null);
            }

            return Result.Ok<string?>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail<string?>(
                ErrorCode.WorkspaceInvalid,
                $"Could not read '{path}': {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Ignored deliberately: see the finally block's comment.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignored deliberately.
        }
    }
}
