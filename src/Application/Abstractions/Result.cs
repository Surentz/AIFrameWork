namespace AiFramework.Application.Abstractions;

/// <summary>How a failure should be surfaced at the HTTP boundary.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
}

/// <summary>An expected failure. Exceptions are for genuinely exceptional conditions.</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message);

/// <summary>Factories for <see cref="Result{T}"/>. Non-generic so call sites infer T.</summary>
public static class Result
{
    public static Result<T> Success<T>(T value) => new(value, null, isSuccess: true);

    public static Result<T> Failure<T>(Error error) => new(default, error, isSuccess: false);
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
