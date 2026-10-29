using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api;

/// <summary>
/// Gives a body to the error responses nothing else writes one for: the cookie handler's 401 and
/// 403 (<c>OnRedirectToLogin</c>/<c>OnRedirectToAccessDenied</c> set only the status) and routing's
/// 404 and 405. Shaped like <see cref="ResultExtensions.Problem{T}"/>'s output - a machine-readable
/// code as the title, and the trace id - so a client handles one error shape, whatever produced it.
/// UseStatusCodePages runs this only for an empty 4xx/5xx, so a controller's own ProblemDetails, the
/// rate limiter's 429 and <see cref="GlobalExceptionHandler"/>'s 500 are left as written.
/// </summary>
internal static class StatusCodeProblems
{
    public static async Task WriteAsync(StatusCodeContext context)
    {
        var httpContext = context.HttpContext;
        var status = httpContext.Response.StatusCode;

        var (title, detail) = status switch
        {
            StatusCodes.Status401Unauthorized => ("auth.unauthenticated", "Sign in to continue."),
            StatusCodes.Status403Forbidden => ("auth.forbidden", "You do not have access to this."),
            StatusCodes.Status404NotFound => ("route.not_found", "Nothing is at this address."),
            StatusCodes.Status405MethodNotAllowed => (
                "route.method_not_allowed", $"This address does not accept {httpContext.Request.Method}."),
            _ => ($"http.{status}", ReasonPhrase(status)),
        };

        var problemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // Through IProblemDetailsService, as GlobalExceptionHandler writes, so the content type is
        // application/problem+json.
        await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problemDetails })
            .ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) =>
        Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } phrase
            ? $"{phrase}."
            : $"The request failed with status {status}.";
}
