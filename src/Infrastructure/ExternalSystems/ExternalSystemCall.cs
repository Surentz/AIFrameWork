using System.Net;
using AiFramework.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Refit;

namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// The one way a partner adapter turns a Refit answer into a <see cref="Result{T}"/> (ADR 0031),
/// so no adapter re-derives it. It sees only the FINAL outcome: retries, timeouts, the circuit
/// breaker and the 401 resend have all run inside the client before this reads anything.
/// </summary>
/// <remarks>
/// <para>
/// Refit 16 does not throw for a failed send behind <c>IApiResponse</c>: what
/// <c>HttpClient.SendAsync</c> threw — a refused connection, a Polly timeout (both tested), an open
/// circuit (thrown from the same pipeline, not tested: the breaker needs 100 calls to trip) —
/// arrives as an <see cref="ApiRequestException"/> with <see cref="IApiResponse.IsReceived"/>
/// false, and is the partner being unavailable. The caller's own cancellation is the exception:
/// Refit rethrows it, so it propagates as <see cref="OperationCanceledException"/>, never a Result.
/// </para>
/// <para>
/// Every failure an adapter did not name is <see cref="ErrorKind.Unavailable"/> (503 at the API,
/// with the partner's own Retry-After when it sent one). So are the statuses an adapter may not
/// name, whatever it says: 401 and 403, because by the time one survives the resend our
/// credentials or the IdP are at fault, never the caller's request; and 408, 429 and every 5xx,
/// which are transient, never a business answer. The message names the system only — never a
/// host, path or body, because it reaches the browser — and the reason goes to the log instead,
/// as an exception type or a status, never more.
/// </para>
/// </remarks>
internal static partial class ExternalSystemCall
{
    public const string UnavailableCode = "external_system.unavailable";

    /// <summary>
    /// A call whose success carries a body, mapped by <c>map</c>. <c>expected</c> names the
    /// partner's documented failures for this call — e.g. 404 → <see cref="ErrorKind.NotFound"/> —
    /// returning null for any other status; pass null when there are none. The response is
    /// disposed when <c>map</c> returns, so a streamed body must be consumed inside it.
    /// </summary>
    public static async Task<Result<T>> SendAsync<TBody, T>(
        string system,
        ILogger logger,
        Func<CancellationToken, Task<IApiResponse<TBody>>> send,
        Func<TBody, Result<T>> map,
        Func<HttpStatusCode, Error?>? expected,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(map);

        using var response = await send(cancellationToken).ConfigureAwait(false);
        if (Failure<T>(system, logger, response, expected) is { } failure)
        {
            return failure;
        }

        if (response.IsSuccessfulWithContent)
        {
            return map(response.Content);
        }

        // A 2xx with no body is not the partner's contract for a call that returns one.
        LogNoBody(logger, system, (int?)response.StatusCode);
        return Unavailable<T>(system, retryAfter: null);
    }

    /// <summary>A call whose success carries no body (a 204, an accepted write): true on any 2xx.</summary>
    public static async Task<Result<bool>> SendAsync(
        string system,
        ILogger logger,
        Func<CancellationToken, Task<IApiResponse>> send,
        Func<HttpStatusCode, Error?>? expected,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(send);

        using var response = await send(cancellationToken).ConfigureAwait(false);
        return Failure<bool>(system, logger, response, expected) ?? Result.Success(true);
    }

    private static Result<T>? Failure<T>(
        string system, ILogger logger, IApiResponse response, Func<HttpStatusCode, Error?>? expected)
    {
        if (!response.IsReceived)
        {
            // Every exception the handler chain threw lands here, a bug in our own handlers
            // included, so the log names its type; its message could carry a host.
            var cause = response.Error?.InnerException ?? response.Error;
            if (logger.IsEnabled(LogLevel.Information))
            {
                // CA1873 does not see the IsEnabled guard around a source-generated log method; the
                // call is guarded, so the type lookup runs only when the line is written.
#pragma warning disable CA1873
                LogNotReceived(logger, system, CauseOf(cause));
#pragma warning restore CA1873
            }

            return Unavailable<T>(system, retryAfter: null);
        }

        if (response.IsSuccessful)
        {
            return null;
        }

        // A 2xx that is still not successful: its body is not the partner's shape. Traffic counts
        // it as a success and Polly never saw it, so this log line is the only trace of a partner
        // that changed its contract.
        if (response.IsSuccessStatusCode || response.StatusCode is not { } status)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                LogNotItsContract(logger, system, (int?)response.StatusCode, CauseOf(response.Error));
            }

            return Unavailable<T>(system, retryAfter: null);
        }

        if (!IsNeverTheAdapters(status) && expected?.Invoke(status) is { } error)
        {
            return Result.Failure<T>(error);
        }

        LogFailedStatus(logger, system, (int)status);
        return Unavailable<T>(system, RetryAfter(response));
    }

    private static bool IsNeverTheAdapters(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status >= 500;

    private static Result<T> Unavailable<T>(string system, TimeSpan? retryAfter) =>
        Result.Failure<T>(new Error(ErrorKind.Unavailable, UnavailableCode, $"{system} is unavailable.", RetryAfter: retryAfter));

    /// <summary>The partner's own Retry-After on the final response, as seconds or as an HTTP date.</summary>
    private static TimeSpan? RetryAfter(IApiResponse response)
    {
        var header = response.Headers?.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date && date - DateTimeOffset.UtcNow is { Ticks: > 0 } remaining ? remaining : null;
    }

    /// <summary>
    /// An exception's type, never its message, which could carry a host or a path. Logged as the
    /// Type itself, so the name is only rendered when the line is written.
    /// </summary>
    private static Type? CauseOf(Exception? exception) => exception?.GetType();

    [LoggerMessage(Level = LogLevel.Information, Message = "{System} is unavailable: no response ({Cause})")]
    private static partial void LogNotReceived(ILogger logger, string system, Type? cause);

    [LoggerMessage(Level = LogLevel.Information, Message = "{System} is unavailable: it answered {StatusCode}")]
    private static partial void LogFailedStatus(ILogger logger, string system, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{System} answered {StatusCode} with a body that is not its contract ({Cause})")]
    private static partial void LogNotItsContract(ILogger logger, string system, int? statusCode, Type? cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{System} answered {StatusCode} with no body where its contract has one")]
    private static partial void LogNoBody(ILogger logger, string system, int? statusCode);
}
