using System.IO.Compression;
using System.Text;
using Sondergraph.Core;

namespace Sondergraph.Ingest;

/// <summary>
/// A raw source backed by a directory on disk.
/// </summary>
/// <remarks>
/// Used for sources placed on disk directly (the standalone <c>contacts.vcf</c>, or an
/// export the user already extracted). For zips, prefer <see cref="ZipRawSource"/> —
/// see <c>docs/platforms/README.md</c>.
/// </remarks>
public sealed class DirectoryRawSource : IRawSource
{
    private readonly string _root;

    /// <summary>
    /// The single file this source exposes, or null when it covers a whole directory.
    /// </summary>
    /// <remarks>
    /// Users routinely want to ingest one file — <c>contacts.vcf</c> is the obvious case, and
    /// it sits loose in an export directory rather than in a folder of its own. Requiring
    /// them to create a directory for it would be a needless ceremony, and pointing at the
    /// file directly is the natural thing to type.
    /// </remarks>
    private readonly string? _singleFile;

    /// <summary>Creates a source over a directory, or over a single file within one.</summary>
    /// <param name="root">A directory, or a path to one file.</param>
    public DirectoryRawSource(string root)
    {
        var full = Path.GetFullPath(root);

        if (File.Exists(full))
        {
            // The file's own directory becomes the root, so relative paths inside the
            // source are just the file name. That keeps sibling lookup working for things
            // like vCard photo files.
            _singleFile = Path.GetFileName(full);
            _root = Path.GetDirectoryName(full) ?? full;
            return;
        }

        _root = full;
    }

    /// <inheritdoc />
    public string Description => _singleFile is null ? _root : Path.Combine(_root, _singleFile);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles()
    {
        if (_singleFile is not null)
        {
            yield return _singleFile;
            yield break;
        }

        if (!Directory.Exists(_root))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            yield return Path.GetRelativePath(_root, file).Replace('\\', '/');
        }
    }

    /// <inheritdoc />
    public Stream? OpenRead(string relativePath)
    {
        var full = ResolveInside(relativePath);

        if (full is null || !File.Exists(full))
        {
            return null;
        }

        try
        {
            return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string? ReadAllText(string relativePath)
    {
        var full = ResolveInside(relativePath);

        if (full is null || !File.Exists(full))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool Exists(string relativePath)
    {
        var full = ResolveInside(relativePath);
        return full is not null && File.Exists(full);
    }

    /// <summary>
    /// Resolves a relative path, refusing to escape the root.
    /// </summary>
    /// <remarks>
    /// Paths come from parsers reading untrusted archive contents, so a traversal like
    /// <c>../../etc/passwd</c> must not resolve. Archive entries are attacker-controllable
    /// in principle even when the archive is the user's own export.
    /// </remarks>
    private string? ResolveInside(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var combined = Path.GetFullPath(Path.Combine(_root, relativePath));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        return combined.StartsWith(rootWithSeparator, StringComparison.Ordinal)
            ? combined
            : null;
    }
}

/// <summary>
/// A raw source backed by a zip archive.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the preferred source for export bundles.</b> Reading the archive directly is
/// not a convenience — an extracted tree is a derived artifact that can be partial,
/// interrupted, or overwritten by a later export, and in this very dataset reading an
/// extracted Facebook tree silently hid a 179-contact file
/// (<c>docs/platforms/README.md</c>).
/// </para>
/// <para>
/// The archive is held open for the lifetime of the source, so it must be disposed. Entries
/// are enumerated from the central directory, which is cheap even for large archives, and
/// individual entries are decompressed on demand.
/// </para>
/// </remarks>
public sealed class ZipRawSource : IRawSource, IDisposable
{
    private readonly ZipArchive _archive;
    private readonly string _path;
    private readonly string? _prefix;
    private bool _disposed;

    private ZipRawSource(ZipArchive archive, string path, string? prefix)
    {
        _archive = archive;
        _path = path;
        _prefix = prefix;
    }

    /// <summary>
    /// Opens a zip archive as a raw source.
    /// </summary>
    /// <param name="zipPath">Path to the archive.</param>
    /// <param name="prefix">
    /// Optional subdirectory to scope to, e.g. <c>Takeout/Contacts</c>. When null, the whole
    /// archive is visible with entry paths as-is.
    /// </param>
    public static Result<ZipRawSource> Open(string zipPath, string? prefix = null)
    {
        try
        {
            var archive = ZipFile.OpenRead(zipPath);
            return Result.Ok(new ZipRawSource(archive, zipPath, NormalisePrefix(prefix)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Result.Fail<ZipRawSource>(
                ErrorCode.WorkspaceInvalid,
                $"Could not open '{zipPath}' as a zip archive: {ex.Message}",
                "Confirm the file is a complete download and not a partial transfer.");
        }
    }

    /// <inheritdoc />
    public string Description => _prefix is null ? _path : $"{_path}!{_prefix}";

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles()
    {
        foreach (var entry in _archive.Entries)
        {
            // Directory entries have no name and zero length.
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relative = Strip(entry.FullName);

            if (relative is not null)
            {
                yield return relative;
            }
        }
    }

    /// <inheritdoc />
    public Stream? OpenRead(string relativePath)
    {
        var entry = Find(relativePath);

        if (entry is null)
        {
            return null;
        }

        try
        {
            return entry.Open();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string? ReadAllText(string relativePath)
    {
        using var stream = OpenRead(relativePath);

        if (stream is null)
        {
            return null;
        }

        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool Exists(string relativePath) => Find(relativePath) is not null;

    /// <summary>Finds an entry by its archive-relative path, case-insensitively.</summary>
    /// <remarks>
    /// Case-insensitive because Windows-produced archives and Linux filesystems disagree,
    /// and Meta's exports contain names differing only by case across platforms.
    /// </remarks>
    private ZipArchiveEntry? Find(string relativePath)
    {
        var wanted = relativePath.Replace('\\', '/').TrimStart('/');

        foreach (var entry in _archive.Entries)
        {
            var relative = Strip(entry.FullName);

            if (relative is not null
                && string.Equals(relative, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Strips the scoping prefix from an entry name, or null when outside the scope.
    /// </summary>
    private string? Strip(string fullName)
    {
        var normalised = fullName.Replace('\\', '/');

        if (_prefix is null)
        {
            return normalised;
        }

        if (!normalised.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalised[_prefix.Length..].TrimStart('/');
    }

    private static string? NormalisePrefix(string? prefix) =>
        string.IsNullOrWhiteSpace(prefix)
            ? null
            : prefix.Replace('\\', '/').Trim('/') + "/";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _archive.Dispose();
    }
}
