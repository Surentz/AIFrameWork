using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic.Models.Beta.Messages;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// One content block of a model turn, as stored. Thinking text, signatures and tool input are kept
/// exactly; a block type this code does not know is kept as its raw JSON and replayed unchanged,
/// because dropping it would edit the history the provider checks (preserved thinking). ADR 0033.
/// </summary>
internal sealed record StoredBlock(
    string Type,
    string? Text = null,
    string? Thinking = null,
    string? Signature = null,
    string? Data = null,
    string? Id = null,
    string? Name = null,
    JsonElement? Input = null,
    JsonElement? Raw = null);

/// <summary>What <c>ModelTurn.Blocks</c> holds: our own JSON, not the SDK's, so an SDK upgrade cannot change what was stored.</summary>
internal static class StoredBlocks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(IReadOnlyList<StoredBlock> blocks) => JsonSerializer.Serialize(blocks, Json);

    public static List<BetaContentBlockParam> ToParams(string blocks) =>
        [.. (JsonSerializer.Deserialize<List<StoredBlock>>(blocks, Json) ?? []).Select(ToParam)];

    private static BetaContentBlockParam ToParam(StoredBlock block) => block.Type switch
    {
        "text" => new BetaTextBlockParam { Text = block.Text ?? string.Empty },
        "thinking" => new BetaThinkingBlockParam { Thinking = block.Thinking ?? string.Empty, Signature = block.Signature ?? string.Empty },
        "redacted_thinking" => new BetaRedactedThinkingBlockParam { Data = block.Data ?? string.Empty },
        "tool_use" => new BetaToolUseBlockParam
        {
            ID = block.Id!,
            Name = block.Name!,
            Input = block.Input!.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
        },
        // A pass-through: the raw constructor serializes the element as-is (Validate() would fail
        // on it, so nothing here calls Validate()).
        _ => new BetaContentBlockParam(block.Raw!.Value),
    };
}
