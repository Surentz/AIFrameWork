using System.Diagnostics;
using System.Globalization;
using AiFramework.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api;

public static class ResultExtensions
{
    // The floor used when a failed Result carries ErrorKind.Unavailable but no RetryAfter of its
    // own. A resilience pipeline that has just exhausted a retry budget often knows a better
    // number - a provider's own Retry-After, or the pipeline's next backoff - but a producer
    // that does not is not exempted from the header; AuthRateLimitTests already proves a 429
    // with no Retry-After leaves a client guessing, and a 503 with none would be the same gap.
    // ADR 0014.
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>Maps a failed Result to RFC 9457 ProblemDetails with the right status code.</summary>
    public static ActionResult Problem<T>(this Result<T> result, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(httpContext);

        var status = result.Error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            // A non-positive value from the producer is treated the same as absent: Math.Ceiling
            // of zero or less would emit "Retry-After: 0" or a negative header, and the rate
            // limiter's own comment on this same computation explains why that is worse than a
            // made-up floor - it sends a well-behaved client straight back into another failure.
            var retryAfter = result.Error.RetryAfter is { } configured && configured > TimeSpan.Zero
                ? configured
                : DefaultRetryAfter;

            // Ceiling, not a cast, for the same reason: truncating the remaining fraction of a
            // second is how "Retry-After: 0" happens.
            httpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = result.Error.Code,
            Detail = result.Error.Message,
        };

        // Lets an operator holding a problem report find the matching log line.
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (result.Error.Details is { Count: > 0 } details)
        {
            problemDetails.Extensions["errors"] = details;
        }

        return new ObjectResult(problemDetails)
        {
            StatusCode = status,
        };
    }
}
