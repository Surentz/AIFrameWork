using System.Diagnostics.Metrics;
using AiFramework.Application.Assistant;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// Token spend and tool use. Prometheus sees aiframework_assistant_tokens_total{kind} and
/// aiframework_assistant_tool_calls_total{tool}. Question outcomes need no metric: every
/// AskAssistant dispatch is already command traffic (Behaviors.LoggedAsync).
/// </summary>
public sealed class AssistantMetrics
{
    public const string MeterName = "AiFramework.Assistant";

    private readonly Counter<long> _tokens;
    private readonly Counter<long> _toolCalls;

    public AssistantMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);

        // The factory owns the meter and disposes it with the container; disposing it here would unpublish it.
#pragma warning disable CA2000
        var meter = meters.Create(MeterName);
#pragma warning restore CA2000
        _tokens = meter.CreateCounter<long>("aiframework.assistant.tokens", unit: "{token}", description: "Tokens used by the ops assistant, by kind.");
        _toolCalls = meter.CreateCounter<long>("aiframework.assistant.tool_calls", unit: "{call}", description: "Tool calls the model asked for, by tool.");
    }

    internal void Record(AssistantModelTurn turn)
    {
        Add("input", turn.Usage.InputTokens);
        Add("output", turn.Usage.OutputTokens);
        Add("cache_read", turn.Usage.CacheReadTokens);
        Add("cache_write", turn.Usage.CacheWriteTokens);
        foreach (var call in turn.ToolCalls)
        {
            _toolCalls.Add(1, new KeyValuePair<string, object?>("tool", call.Name));
        }
    }

    private void Add(string kind, long tokens)
    {
        if (tokens > 0)
        {
            _tokens.Add(tokens, new KeyValuePair<string, object?>("kind", kind));
        }
    }
}
