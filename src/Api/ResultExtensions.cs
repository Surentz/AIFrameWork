using System.Diagnostics;
using AiFramework.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api;

public static class ResultExtensions
{
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
            _ => StatusCodes.Status500InternalServerError,
        };

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
