namespace AiFramework.Application.Abstractions;

// MA0048 (file name must match type name) fires because ErrorKind, Error and
// Result<T> are co-located in Result.cs. Task 2's brief and scope explicitly
// pin this file to hold all three types together, and later tasks bind to the
// AiFramework.Application.Abstractions.{Error, ErrorKind, Result<T>} names as
// given, so splitting into per-type files is out of scope here.
#pragma warning disable MA0048

/// <summary>How a failure should be surfaced at the HTTP boundary.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
}

// CA1716 asks to rename `Error` because it shadows the `error` contextual
// keyword. The name is a fixed part of Task 2's public contract (later tasks
// bind to it as `AiFramework.Application.Abstractions.Error`), so it stays.
#pragma warning disable CA1716

/// <summary>An expected failure. Exceptions are for genuinely exceptional conditions.</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message);

#pragma warning restore CA1716

/// <summary>The outcome of a use case that produces a value.</summary>
public sealed class Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T value)
    {
        _value = value;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        _error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read Value of a failed Result.");

    public Error Error => IsSuccess
        ? throw new InvalidOperationException("Cannot read Error of a successful Result.")
        : _error!;

    // CA1000 asks to move these static factories off the generic type. They
    // are Task 2's specified contract (`Result<T>.Success`, `Result<T>.Failure`)
    // and later tasks bind to them by that exact call shape, so they stay.
#pragma warning disable CA1000
    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error) => new(error);
#pragma warning restore CA1000
}

#pragma warning restore MA0048
