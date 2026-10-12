using System.Text.Json;
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using Microsoft.Extensions.Options;

namespace AiFramework.Api.Assistant;

/// <summary>
/// Writes the answer as server-sent events. Starts the response lazily, on the first write, so a
/// failure before then is still an ordinary ProblemDetails response. Flushes every write, and
/// serialises them: the keep-alive runs alongside the answer.
/// </summary>
public sealed class SseAssistantOutput(IHttpContextAccessor accessor, IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> json, AssistantAvailability availability)
    : IAssistantOutput, IDisposable
{
    private const string KeepAlive = ": keep-alive\n\n";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Started { get; private set; }

    public Task TextAsync(string delta, CancellationToken cancellationToken) =>
        WriteAsync(AssistantEventResponse.Text(delta), cancellationToken);

    public Task ToolAsync(string toolName, CancellationToken cancellationToken) =>
        WriteAsync(AssistantEventResponse.ToolStarted(toolName), cancellationToken);

    public Task AnswerAsync(QuickAnswer answer, CancellationToken cancellationToken) =>
        WriteAsync(AssistantEventResponse.Answered(AssistantCards.ToResponse(answer)), cancellationToken);

    public Task SuggestionsAsync(QuickSuggestions suggestions, CancellationToken cancellationToken) =>
        WriteAsync(AssistantEventResponse.Suggested(AssistantCards.ToResponse(suggestions, availability.AiEnabled)), cancellationToken);

    public Task WriteAsync(AssistantEventResponse streamEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        var data = JsonSerializer.Serialize(streamEvent, json.Value.JsonSerializerOptions);
        return WriteRawAsync($"event: {streamEvent.Type}\ndata: {data}\n\n", cancellationToken);
    }

    /// <summary>
    /// Writes an SSE comment every <paramref name="interval"/> until cancelled. A proxy closes a
    /// connection that stays silent past its read timeout (ingress-nginx: 60 s by default), and a
    /// model can think that long before its first visible word or between tools. Clients ignore
    /// comments. The first one starts the stream, so a failure after it arrives as an error event.
    /// </summary>
    public async Task KeepAliveAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteRawAsync(KeepAlive, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped: the answer is complete, or the admin went away.
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task WriteRawAsync(string text, CancellationToken cancellationToken)
    {
        var response = accessor.HttpContext?.Response
            ?? throw new InvalidOperationException("The assistant output has no HTTP response to write to.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

            await response.WriteAsync(text, cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
