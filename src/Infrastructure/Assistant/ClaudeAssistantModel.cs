using System.Text.Json;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using Anthropic;
using Anthropic.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// The model port over the official Anthropic SDK: beta Messages API, streaming. Retry is the
/// SDK's own (MaxRetries); the HttpClient carries OutboundTrafficHandler so calls show on the
/// Integrations page and in the external-system call metric. Not an ExternalSystemsBuilder client:
/// that builder is Refit-only, and its resilience handler would retry a streaming POST and cut
/// long streams at its attempt timeout. ADR 0033.
/// </summary>
/// <remarks>
/// Every SDK failure becomes a Result, never an exception: after the first SSE byte an exception
/// can only reset the admin's connection. A turn that could not be replayed (see
/// <see cref="AssistantStreamAccumulator.IsReplayable"/>) is reported incomplete, never stored.
/// </remarks>
internal sealed partial class ClaudeAssistantModel(
    IHttpClientFactory httpClients,
    IOptions<AssistantOptions> options,
    AssistantMetrics metrics,
    ILogger<ClaudeAssistantModel> logger) : IAssistantModel
{
    public const string HttpClientName = "anthropic";

    /// <summary>The traffic and metric name, as on the Integrations page.</summary>
    public const string SystemName = "Anthropic";

    public async Task<Result<AssistantModelTurn>> NextTurnAsync(
        AssistantRequest request, IAssistantOutput output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);

        var settings = options.Value;
        var apiKey = await File.ReadAllTextAsync(settings.ApiKeyFile!, cancellationToken).ConfigureAwait(false);
        using var client = new AnthropicClient
        {
            HttpClient = httpClients.CreateClient(HttpClientName),
            ApiKey = apiKey.Trim(),
            BaseUrl = settings.BaseUrl,
            MaxRetries = settings.MaxRetries,
        };

        try
        {
            return await StreamAsync(client, request, settings, output, cancellationToken).ConfigureAwait(false);
        }
        catch (AnthropicRateLimitException)
        {
            return Unavailable();
        }
        catch (Anthropic5xxException)
        {
            return Unavailable();
        }
        catch (AnthropicIOException)
        {
            return Unavailable();
        }
        catch (AnthropicSseException)
        {
            return Unavailable();
        }
        catch (AnthropicInvalidDataException)
        {
            return Unavailable();
        }
        catch (AnthropicUnexpectedStatusCodeException)
        {
            return Unavailable();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable();
        }
        catch (JsonException)
        {
            return Incomplete();
        }
        catch (Anthropic4xxException exception)
        {
            // Our request was wrong: a bug, or a stored history the provider no longer accepts.
            // Logged (without the request: it holds the admin's question), surfaced as a 503 that
            // says what to do, never as a 500.
            LogRejected(logger, exception.GetType().Name);
            return Result.Failure<AssistantModelTurn>(new Error(
                ErrorKind.Unavailable, "assistant.rejected", "The assistant could not continue this conversation. Start a new conversation."));
        }
    }

    private static Result<AssistantModelTurn> Unavailable() =>
        Result.Failure<AssistantModelTurn>(new Error(ErrorKind.Unavailable, "assistant.unavailable", "The assistant is unavailable. Try again shortly."));

    private static Result<AssistantModelTurn> Incomplete() =>
        Result.Failure<AssistantModelTurn>(new Error(ErrorKind.Unavailable, "assistant.incomplete", "The assistant stopped part-way through. Try again."));

    [LoggerMessage(Level = LogLevel.Error, Message = "The model provider rejected an assistant request ({ExceptionType}).")]
    private static partial void LogRejected(ILogger logger, string exceptionType);

    private async Task<Result<AssistantModelTurn>> StreamAsync(
        AnthropicClient client, AssistantRequest request, AssistantOptions settings, IAssistantOutput output, CancellationToken cancellationToken)
    {
        var accumulator = new AssistantStreamAccumulator();
        var events = client.Beta.Messages.CreateStreaming(AnthropicRequestMapper.ToParams(request, settings), cancellationToken);
        await foreach (var streamEvent in events.ConfigureAwait(false))
        {
            if (accumulator.Apply(streamEvent) is { } text)
            {
                await output.TextAsync(text, cancellationToken).ConfigureAwait(false);
            }
        }

        var turn = accumulator.Complete();
        metrics.Record(turn);
        return accumulator.IsReplayable ? Result.Success(turn) : Incomplete();
    }
}
