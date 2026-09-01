using System.Diagnostics;
using AiFramework.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api;

public sealed partial class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problemDetailsService)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // Setting StatusCode (or writing) after the response has begun streaming throws.
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        var (status, title, detail) = exception switch
        {
            DomainException domain => (
                StatusCodes.Status400BadRequest, "domain.invariant_violated", domain.Message),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "An error occurred."),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            LogUnhandledException(logger, exception);
        }

        httpContext.Response.StatusCode = status;

        var problemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail };

        // Lets an operator holding a 500 report find the matching log line.
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // Writing through IProblemDetailsService (rather than a raw WriteAsJsonAsync) is what
        // makes the registered AddProblemDetails() service actually run - it is what sets the
        // RFC 9457 application/problem+json content type instead of a plain application/json.
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        }).ConfigureAwait(false);
    }

    // CA1848: log the message via the source-generated LoggerMessage delegate rather than
    // calling ILogger.LogError directly.
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception);
}
