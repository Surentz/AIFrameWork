using System.Text;
using System.Text.Json;
using AiFramework.Application.Assistant;
using Anthropic.Models.Beta.Messages;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// Rebuilds one model turn from the Messages API's stream events: content blocks by index, tool
/// input from its JSON fragments, the stop reason and the usage. Returns each text delta so the
/// caller can forward it as it arrives.
/// </summary>
internal sealed class AssistantStreamAccumulator
{
    private readonly SortedDictionary<long, Builder> _blocks = [];
    private AssistantStopReason _stop = AssistantStopReason.EndTurn;
    private long _input;
    private long _output;
    private long _cacheRead;
    private long _cacheWrite;
    private bool _malformed;

    /// <summary>
    /// False when the turn cannot be replayed to the provider: a delta arrived for a block that never
    /// started, a thinking block never got its signature (max_tokens landed inside it), or a text
    /// block is empty. Stored, such a turn would make every later question in the conversation fail,
    /// so the adapter reports it as incomplete instead. Read after <see cref="Complete"/>.
    /// </summary>
    public bool IsReplayable { get; private set; }

    /// <summary>The visible text this event adds, or null.</summary>
    public string? Apply(BetaRawMessageStreamEvent streamEvent)
    {
        if (streamEvent.TryPickStart(out var start))
        {
            var usage = start.Message.Usage;
            ReadUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens, usage.CacheCreationInputTokens);
            return null;
        }

        if (streamEvent.TryPickContentBlockStart(out var blockStart))
        {
            _blocks[blockStart.Index] = Builder.Start(blockStart.ContentBlock);
            return null;
        }

        if (streamEvent.TryPickContentBlockDelta(out var delta))
        {
            if (!_blocks.TryGetValue(delta.Index, out var block))
            {
                _malformed = true;
                return null;
            }

            return block.Apply(delta.Delta);
        }

        if (streamEvent.TryPickDelta(out var messageDelta))
        {
            _stop = MapStop(messageDelta.Delta.StopReason?.Raw());
            var usage = messageDelta.Usage;
            ReadUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens, usage.CacheCreationInputTokens);
        }

        return null;
    }

    public AssistantModelTurn Complete()
    {
        var blocks = _blocks.Values.Select(b => b.Build()).ToList();
        var calls = blocks
            .Where(b => string.Equals(b.Type, "tool_use", StringComparison.Ordinal))
            .Select(b => new AssistantToolCall(b.Id!, b.Name!, b.Input!.Value))
            .ToList();

        IsReplayable = !_malformed && blocks.TrueForAll(IsReplayableBlock);

        return new AssistantModelTurn(
            new ModelTurn(StoredBlocks.Serialize(blocks)), calls, _stop, new AssistantUsage(_input, _output, _cacheRead, _cacheWrite));
    }

    private static bool IsReplayableBlock(StoredBlock block) => block.Type switch
    {
        "thinking" => !string.IsNullOrEmpty(block.Signature),
        "text" => !string.IsNullOrEmpty(block.Text),
        _ => true,
    };

    private static AssistantStopReason MapStop(string? reason) => reason switch
    {
        "tool_use" => AssistantStopReason.ToolUse,
        "max_tokens" => AssistantStopReason.MaxTokens,
        "refusal" => AssistantStopReason.Refusal,
        _ => AssistantStopReason.EndTurn,
    };

    private void ReadUsage(long? input, long? output, long? cacheRead, long? cacheWrite)
    {
        _input = Math.Max(_input, input ?? 0);
        _output = Math.Max(_output, output ?? 0);
        _cacheRead = Math.Max(_cacheRead, cacheRead ?? 0);
        _cacheWrite = Math.Max(_cacheWrite, cacheWrite ?? 0);
    }

    private sealed class Builder
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _json = new();
        private string _type = "unknown";
        private string? _signature;
        private string? _data;
        private string? _id;
        private string? _name;
        private JsonElement? _raw;

        public static Builder Start(ContentBlock block)
        {
            var builder = new Builder();
            if (block.TryPickBetaText(out _))
            {
                builder._type = "text";
            }
            else if (block.TryPickBetaThinking(out var thinking))
            {
                builder._type = "thinking";
                builder._text.Append(thinking.Thinking);
                builder._signature = thinking.Signature;
            }
            else if (block.TryPickBetaRedactedThinking(out var redacted))
            {
                builder._type = "redacted_thinking";
                builder._data = redacted.Data;
            }
            else if (block.TryPickBetaToolUse(out var toolUse))
            {
                builder._type = "tool_use";
                builder._id = toolUse.ID;
                builder._name = toolUse.Name;
            }
            else
            {
                builder._raw = block.Json.Clone();
            }

            return builder;
        }

        public string? Apply(BetaRawContentBlockDelta delta)
        {
            if (delta.TryPickText(out var text))
            {
                _text.Append(text.Text);
                return text.Text;
            }

            if (delta.TryPickThinking(out var thinking))
            {
                _text.Append(thinking.Thinking);
            }
            else if (delta.TryPickSignature(out var signature))
            {
                _signature = signature.Signature;
            }
            else if (delta.TryPickInputJson(out var json))
            {
                _json.Append(json.PartialJson);
            }

            return null;
        }

        public StoredBlock Build() => _type switch
        {
            "text" => new StoredBlock("text", Text: _text.ToString()),
            "thinking" => new StoredBlock("thinking", Thinking: _text.ToString(), Signature: _signature ?? string.Empty),
            "redacted_thinking" => new StoredBlock("redacted_thinking", Data: _data),
            "tool_use" => new StoredBlock("tool_use", Id: _id, Name: _name, Input: ParseInput()),
            _ => new StoredBlock(_raw!.Value.GetProperty("type").GetString() ?? "unknown", Raw: _raw),
        };

        private JsonElement ParseInput()
        {
            using var document = JsonDocument.Parse(_json.Length == 0 ? "{}" : _json.ToString());
            return document.RootElement.Clone();
        }
    }
}
