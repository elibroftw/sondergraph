using Sondergraph.Core;

namespace Sondergraph.Ingest;

/// <summary>
/// Read-only access to whatever an acquirer left behind.
/// </summary>
/// <remarks>
/// <para>
/// Parsers never touch the filesystem directly. They receive one of these, which lets the
/// same parser read from an extracted directory, a zip archive, or an in-memory fixture
/// without knowing the difference — and makes fixture-driven tests trivial (ADR 0003).
/// </para>
/// </remarks>
public interface IRawSource
{
    /// <summary>A human-readable description of where this came from.</summary>
    string Description { get; }

    /// <summary>Enumerates every file, with paths relative to the source root.</summary>
    IEnumerable<string> EnumerateFiles();

    /// <summary>Opens a file for reading, or null when it does not exist.</summary>
    Stream? OpenRead(string relativePath);

    /// <summary>Reads a file as text, or null when it does not exist.</summary>
    string? ReadAllText(string relativePath);

    /// <summary>Whether a file exists.</summary>
    bool Exists(string relativePath);
}

/// <summary>
/// Context a parser may consult, and the channel for reporting problems.
/// </summary>
/// <remarks>
/// <see cref="Report"/> is how a parser says "I skipped this, and here is why" without
/// throwing. Progress and problems are callbacks rather than console writes, per the
/// thin-shell rule (ADR 0001).
/// </remarks>
public sealed class ParseContext
{
    private readonly List<ParseProblem> _problems = [];
    private readonly Action<ParseProblem>? _onProblem;

    /// <summary>Creates a context.</summary>
    /// <param name="onProblem">Optional sink, for streaming problems as they occur.</param>
    public ParseContext(Action<ParseProblem>? onProblem = null)
    {
        _onProblem = onProblem;
    }

    /// <summary>
    /// Parser options, as free-form key/value pairs.
    /// </summary>
    /// <remarks>
    /// Deliberately loose rather than a typed settings object: parsers are pure functions of
    /// their input, and only a few knobs are ever worth exposing. A typed class per parser
    /// would add ceremony without removing the real problem, which is that an option changes
    /// what gets extracted and therefore needs recording in the ingest report.
    /// </remarks>
    public IDictionary<string, string> Options { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a boolean option, falling back to a supplied default.</summary>
    public bool GetFlag(string key, bool defaultValue) =>
        Options.TryGetValue(key, out var raw)
            ? !(raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("0", StringComparison.Ordinal)
                || raw.Equals("no", StringComparison.OrdinalIgnoreCase))
            : defaultValue;

    /// <summary>When the parse started, fixed once so parsers are reproducible.</summary>
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>Everything skipped during this parse.</summary>
    public IReadOnlyList<ParseProblem> Problems => _problems;

    /// <summary>Records a skipped or partially-parsed record.</summary>
    public void Report(string sourcePath, string reason, string? detail = null)
    {
        var problem = new ParseProblem(sourcePath, reason, detail);
        _problems.Add(problem);
        _onProblem?.Invoke(problem);
    }
}

/// <summary>
/// Reads one platform's raw dump and yields platform-neutral records.
/// </summary>
/// <remarks>
/// <para>
/// <b>A parser is a pure function from files to records</b> (ADR 0003). It performs no
/// network calls, reads no credentials, consults no clock, and writes nothing. That is what
/// makes a parse reproducible and re-runnable: a parser bug is fixed by re-parsing, not by
/// re-fetching an export that may take days to regenerate.
/// </para>
/// <para>
/// Rules that are acceptance criteria, not guidelines:
/// <list type="number">
/// <item>Never invent an id — derive deterministically and say so.</item>
/// <item>Never rewrite the raw dump; parsers only read.</item>
/// <item>Never resolve identity; emit observations.</item>
/// <item>Never fail the whole parse for one bad record; report and continue.</item>
/// <item>Declare a <see cref="ParserVersion"/>.</item>
/// <item>No network, no credentials, no clock.</item>
/// </list>
/// </para>
/// </remarks>
public interface IPlatformParser
{
    /// <summary>Platform key, matching the raw dump directory name.</summary>
    string Platform { get; }

    /// <summary>Human-readable name for display.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Version of this parser's output for a given input.
    /// </summary>
    /// <remarks>
    /// Bump when a parser change alters what is produced from unchanged input. This makes
    /// "why did my data change after an upgrade?" answerable, and lets re-parsing be
    /// skipped when nothing has changed.
    /// </remarks>
    int ParserVersion { get; }

    /// <summary>
    /// Whether this parser recognises the given source.
    /// </summary>
    /// <remarks>
    /// Checked by the registry before parsing, so several parsers can compete for one dump
    /// directory. Should be cheap and must not throw.
    /// </remarks>
    bool CanParse(IRawSource source);

    /// <summary>
    /// Yields every record found in the source.
    /// </summary>
    /// <remarks>
    /// Lazy on purpose: a 726 MB mailbox must not be materialised into a list before the
    /// caller can start work, and callers may want to stop early.
    /// </remarks>
    IEnumerable<ParsedRecord> Parse(IRawSource source, ParseContext context);
}
