using Sondergraph.Core;

namespace Sondergraph.Cli;

/// <summary>
/// Renders results to the terminal.
/// </summary>
/// <remarks>
/// Kept in one place so that error formatting is consistent, and so the API can implement
/// the same mapping over HTTP (ADR 0001: the shells differ in presentation, never in logic).
/// The convention is git-like: diagnostics to stderr, data to stdout, so `sg ... | ...`
/// pipes cleanly.
/// </remarks>
internal static class ConsoleRenderer
{
    internal static void WriteError(string message)
    {
        Console.Error.WriteLine($"sg: {message}");
    }

    /// <summary>
    /// Writes a failure, including the hint when there is one.
    /// </summary>
    /// <remarks>
    /// The hint is the actionable part — "run sg init", "use --wait" — and is the main
    /// reason <see cref="Result"/> carries it separately from the message.
    /// </remarks>
    internal static void WriteFailure(ErrorCode code, string message, string? hint)
    {
        WriteError(message);

        if (!string.IsNullOrWhiteSpace(hint))
        {
            Console.Error.WriteLine($"sg: hint: {hint}");
        }
    }

    internal static void WriteFailure(Result result) =>
        WriteFailure(result.Code, result.Message, result.Hint);

    internal static void WriteFailure<T>(Result<T> result) =>
        WriteFailure(result.Code, result.Message, result.Hint);
}
