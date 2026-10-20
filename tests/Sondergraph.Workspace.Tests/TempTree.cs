using System.Diagnostics;

namespace Sondergraph.Workspaces.Tests;

/// <summary>
/// A throwaway directory tree, removed on dispose.
/// </summary>
/// <remarks>
/// Uses a real filesystem rather than an abstraction. This layer's whole job is to be
/// correct about real filesystem semantics — atomic rename, flock, inode identity — none
/// of which a mock would exercise.
/// </remarks>
public sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "sg-test-" + Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string CreateDirectory(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string WriteFile(string relative, string contents)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a leaked temp directory must never fail a test run.
        }
    }
}
