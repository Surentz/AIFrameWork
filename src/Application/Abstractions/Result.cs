namespace AiFramework.Application.Abstractions;

/// <summary>How a failure should be surfaced at the HTTP boundary.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,

    /// <summary>
    /// The caller is not who they claim to be — bad credentials, or no session at all. Distinct
    /// from Validation because a rejected sign-in is not a malformed request, and mapping it to
    /// 400 would tell a client to fix its input when the input was well-formed.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// An expected, retryable upstream failure — a database call or a third-party integration
    /// that failed after exhausting its own retry budget. Distinct from a bug: the request was
    /// well-formed and this server is fine, it is a DEPENDENCY that could not be reached in
    /// time. Mapped to 503 with a Retry-After header, never to 500 — GlobalExceptionHandler's
    /// 500 branch is for exceptions nobody anticipated, and this one is anticipated by name.
    /// ADR 0014.
    /// </summary>
    Unavailable,
}

/// <summary>An expected failure. Exceptions are for genuinely exceptional conditions.</summary>
/// <param name="Kind">How the failure should be surfaced at the HTTP boundary.</param>
/// <param name="Code">A stable, machine-readable identifier, used as the ProblemDetails title.</param>
/// <param name="Message">A human-readable description, used as the ProblemDetails detail.</param>
/// <param name="Details">
/// Optional per-field validation messages, keyed by property name, in the same shape
/// ASP.NET Core's ModelState-driven <c>ValidationProblemDetails.Errors</c> uses. Populated by
/// the validation behavior; most other failures leave it null.
/// </param>
/// <param name="RetryAfter">
/// How long the caller should wait before trying again. Only meaningful alongside
/// <see cref="ErrorKind.Unavailable"/> — every other kind leaves it null and
/// <c>ResultExtensions.Problem</c> ignores it for them. A producer that knows a better number
/// (a provider's own Retry-After, or a resilience pipeline's own next backoff) should set it;
/// left null, the API boundary falls back to a fixed floor rather than omitting the header.
/// ADR 0014.
/// </param>
public sealed record Error(
    ErrorKind Kind,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? Details = null,
    TimeSpan? RetryAfter = null);

/// <summary>Factories for <see cref="Result{T}"/>. Non-generic so call sites infer T.</summary>
public static class Result
{
    public static Result<T> Success<T>(T value) => new(value, null, isSuccess: true);

    public static Result<T> Failure<T>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new(default, error, isSuccess: false);
    }
}

/// <summary>The outcome of a use case that produces a value.</summary>
public sealed class Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    internal Result(T? value, Error? error, bool isSuccess)
    {
        _value = value;
        _error = error;
        IsSuccess = isSuccess;
    }

    public bool IsSuccess { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read Value of a failed Result.");

    public Error Error => IsSuccess
        ? throw new InvalidOperationException("Cannot read Error of a successful Result.")
        : _error!;
}
