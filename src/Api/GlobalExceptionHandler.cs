using AiFramework.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api;

public sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

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
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = status, Title = title, Detail = detail },
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    // CA1848: log the message via the source-generated LoggerMessage delegate rather than
    // calling ILogger.LogError directly.
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception);
}
