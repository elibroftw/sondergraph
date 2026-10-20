using System.Text;
using Sondergraph.Ingest;

namespace Sondergraph.Ingest.Tests.Fakes;

/// <summary>
/// An <see cref="IRawSource"/> backed by an in-memory dictionary.
/// </summary>
/// <remarks>
/// The point of <see cref="IRawSource"/> being an abstraction (ADR 0003): parsers are tested
/// against literal fixture strings with no filesystem, no zip, and no network. Every fixture
/// here is synthetic — invented names and reserved-range phone numbers — because real exports
/// contain other people's personal data and must never be committed.
/// </remarks>
public sealed class InMemoryRawSource : IRawSource
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _binary = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryRawSource(string description = "in-memory fixture")
    {
        Description = description;
    }

    public string Description { get; }

    public InMemoryRawSource Add(string path, string contents)
    {
        _files[path] = contents;
        return this;
    }

    public InMemoryRawSource AddBytes(string path, byte[] contents)
    {
        _binary[path] = contents;
        return this;
    }

    public IEnumerable<string> EnumerateFiles() =>
        _files.Keys.Concat(_binary.Keys).Order(StringComparer.Ordinal);

    public Stream? OpenRead(string relativePath)
    {
        if (_binary.TryGetValue(relativePath, out var bytes))
        {
            return new MemoryStream(bytes, writable: false);
        }

        return _files.TryGetValue(relativePath, out var text)
            ? new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false)
            : null;
    }

    public string? ReadAllText(string relativePath) =>
        _files.TryGetValue(relativePath, out var text) ? text : null;

    public bool Exists(string relativePath) =>
        _files.ContainsKey(relativePath) || _binary.ContainsKey(relativePath);
}
