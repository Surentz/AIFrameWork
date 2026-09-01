using AiFramework.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api;

public static class ResultExtensions
{
    /// <summary>Maps a failed Result to RFC 9457 ProblemDetails with the right status code.</summary>
    public static ActionResult Problem<T>(this Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var status = result.Error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };

        return new ObjectResult(new ProblemDetails
        {
            Status = status,
            Title = result.Error.Code,
            Detail = result.Error.Message,
        })
        {
            StatusCode = status,
        };
    }
}
