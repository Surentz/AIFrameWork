using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AiFramework.Api.Assistant;

/// <summary>
/// Ends the assistant's event stream with an error event when something unanticipated throws after
/// the stream started — a tool query's database fault, say. Without it the exception reaches
/// UseExceptionHandler, which does not run handlers once a response has started: the connection is
/// reset and the admin sees the answer stop with no explanation.
/// </summary>
/// <remarks>
/// An MVC exception filter rather than a catch: it marks the exception handled without the
/// <c>catch (Exception)</c> CA1031 forbids, and applies to this controller only. Before the stream
/// starts it does nothing, so GlobalExceptionHandler's ordinary 500 still applies. The exception
/// message is logged, never written: the event carries the same opaque 500 the handler would.
/// </remarks>
public sealed partial class AssistantStreamExceptionFilter(
    SseAssistantOutput output, ILogger<AssistantStreamExceptionFilter> logger) : IAsyncExceptionFilter
{
    public async Task OnExceptionAsync(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!output.Started || context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        LogStreamFailed(logger, context.Exception);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "internal_error",
            Detail = "An error occurred.",
        };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;

        await output.WriteAsync(AssistantEventResponse.Error(problem), context.HttpContext.RequestAborted).ConfigureAwait(false);
        context.Result = new EmptyResult();
        context.ExceptionHandled = true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The assistant failed after its answer stream started")]
    private static partial void LogStreamFailed(ILogger logger, Exception exception);
}
