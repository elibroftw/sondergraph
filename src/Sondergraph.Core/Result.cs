namespace Sondergraph.Core;

/// <summary>
/// The outcome of an operation, as a value rather than an exception.
/// </summary>
/// <remarks>
/// <para>
/// Core returns these instead of throwing for expected failures (a locked workspace is not
/// exceptional — it is Tuesday). This is what ADR 0001 means by "result-shaped": the CLI
/// decides to print an error and exit non-zero, the API decides to return 409, and neither
/// needs a try/catch to express an ordinary outcome.
/// </para>
/// <para>
/// Exceptions remain appropriate for programmer errors and genuinely unexpected faults.
/// </para>
/// </remarks>
public sealed class Result
{
    private Result(bool ok, ErrorCode code, string message, string? hint)
    {
        IsOk = ok;
        Code = code;
        Message = message;
        Hint = hint;
    }

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsOk { get; }

    /// <summary>Machine-readable failure code; <see cref="ErrorCode.Unknown"/> when OK.</summary>
    public ErrorCode Code { get; }

    /// <summary>Human-readable description. Safe to show to a user.</summary>
    public string Message { get; }

    /// <summary>
    /// Optional actionable next step, e.g. a flag to pass or a command to run.
    /// </summary>
    /// <remarks>
    /// The point of separating this from <see cref="Message"/> is that a hint is advice,
    /// not a diagnosis — the CLI renders it dimmed, the API returns it as a distinct field,
    /// and a script can ignore it without discarding the error.
    /// </remarks>
    public string? Hint { get; }

    /// <summary>Creates a successful result.</summary>
    public static Result Ok() => new(true, ErrorCode.Unknown, string.Empty, null);

    /// <summary>Creates a failed result.</summary>
    public static Result Fail(ErrorCode code, string message, string? hint = null) =>
        new(false, code, message, hint);

    /// <summary>Creates a successful result carrying <paramref name="value"/>.</summary>
    /// <typeparam name="T">Type of the produced value.</typeparam>
    public static Result<T> Ok<T>(T value) =>
        new(true, value, ErrorCode.Unknown, string.Empty, null);

    /// <summary>Creates a failed result of type <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Type of the value that would have been produced.</typeparam>
    public static Result<T> Fail<T>(ErrorCode code, string message, string? hint = null) =>
        new(false, default, code, message, hint);

    /// <inheritdoc />
    public override string ToString() =>
        IsOk ? "ok" : $"{Code}: {Message}";
}

/// <summary>
/// The outcome of an operation that produces a value.
/// </summary>
/// <typeparam name="T">Type of the produced value.</typeparam>
/// <remarks>
/// Constructed via <see cref="Result.Ok{T}(T)"/> and <see cref="Result.Fail{T}"/> rather
/// than static members here, so that <c>T</c> is inferred from the value at the call site
/// instead of being written out (CA1000).
/// </remarks>
public sealed class Result<T> : IDisposable
{
    internal Result(bool ok, T? value, ErrorCode code, string message, string? hint)
    {
        IsOk = ok;
        Value = value;
        Code = code;
        Message = message;
        Hint = hint;
    }

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsOk { get; }

    /// <summary>The value. Only meaningful when <see cref="IsOk"/> is true.</summary>
    public T? Value { get; }

    /// <summary>Machine-readable failure code.</summary>
    public ErrorCode Code { get; }

    /// <summary>Human-readable description.</summary>
    public string Message { get; }

    /// <summary>Optional actionable next step.</summary>
    public string? Hint { get; }

    /// <summary>
    /// Gets the value or throws. For tests and for call sites that have already checked
    /// <see cref="IsOk"/>.
    /// </summary>
    public T ValueOrThrow() =>
        IsOk
            ? Value!
            : throw new InvalidOperationException($"Result is a failure: {Code}: {Message}");

    /// <summary>
    /// Disposes the value if the operation succeeded and it is disposable.
    /// </summary>
    /// <remarks>
    /// This exists so that a successful result holding a resource can be used with a
    /// <c>using</c> declaration — <c>using var r = TryAcquire(...)</c> — which is the
    /// natural way to write it. On failure there is nothing to release, so this is a no-op.
    /// Failures are surfaced when the caller inspects <see cref="IsOk"/>, not here.
    /// </remarks>
    public void Dispose()
    {
        if (IsOk && Value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        IsOk ? $"ok({Value})" : $"{Code}: {Message}";
}
