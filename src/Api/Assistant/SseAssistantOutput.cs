using System.Text.Json;
using AiFramework.Application.Assistant;
using Microsoft.Extensions.Options;

namespace AiFramework.Api.Assistant;

/// <summary>
/// Writes the answer as server-sent events. Starts the response lazily, on the first event, so a
/// failure before then is still an ordinary ProblemDetails response. Flushes every event.
/// </summary>
public sealed class SseAssistantOutput(IHttpContextAccessor accessor, IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> json) : IAssistantOutput
{
    public bool Started { get; private set; }

    public Task TextAsync(string delta, CancellationToken cancellationToken) =>
        WriteAsync(AssistantEventResponse.Text(delta), cancellationToken);

    public Task ToolAsync(string toolName, CancellationToken cancellationToken) =>
        WriteAsync(AssistantEventResponse.ToolStarted(toolName), cancellationToken);

    public async Task WriteAsync(AssistantEventResponse streamEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        var response = accessor.HttpContext?.Response
            ?? throw new InvalidOperationException("The assistant output has no HTTP response to write to.");

        if (!Started)
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";

            // ingress-nginx buffers a response until it ends unless told otherwise; the answer
            // would arrive all at once, or not before the proxy's read timeout.
            response.Headers["X-Accel-Buffering"] = "no";
            Started = true;
        }

        var data = JsonSerializer.Serialize(streamEvent, json.Value.JsonSerializerOptions);
        await response.WriteAsync($"event: {streamEvent.Type}\ndata: {data}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
