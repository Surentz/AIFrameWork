using System.Globalization;
using System.Text.Json;
using AiFramework.Application.Assistant;
using Anthropic.Models.Beta.Messages;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// Builds the Messages API request. Tools and the system prompt come first and never change, and
/// automatic caching (top-level cache_control) caches the whole prefix up to the latest turn, so
/// every follow-up reads the earlier turns from cache.
/// </summary>
internal static class AnthropicRequestMapper
{
    /// <summary>Server-side refusal fallback: a declined request is retried on the model's default fallback.</summary>
    public const string FallbackBeta = "server-side-fallback-2026-07-01";

    public static MessageCreateParams ToParams(AssistantRequest request, AssistantOptions options) => new()
    {
        Model = options.Model,
        MaxTokens = options.MaxTokens,
        Betas = [FallbackBeta],
        Fallbacks = new Default(),
        System = request.SystemPrompt,
        CacheControl = new BetaCacheControlEphemeral(),
        Thinking = new BetaThinkingConfigAdaptive(),
        OutputConfig = new BetaOutputConfig { Effort = options.Effort },
        Tools = [.. request.Tools.Select(ToTool)],

        // Tools stay listed even when not allowed: the history holds tool_use blocks, and an
        // unchanged tool list keeps the cached prefix.
        ToolChoice = request.AllowTools ? new BetaToolChoiceAuto() : new BetaToolChoiceNone(),
        Messages = [.. request.Transcript.Select(ToMessage)],
    };

    private static BetaToolUnion ToTool(AssistantToolDefinition tool)
    {
        using var schema = JsonDocument.Parse(tool.InputSchema);
        var raw = schema.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

        return new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,

            // From the raw schema, so additionalProperties:false survives verbatim.
            InputSchema = InputSchema.FromRawUnchecked(raw),
            Strict = true,
        };
    }

    private static BetaMessageParam ToMessage(AssistantTurn turn) => turn switch
    {
        UserQuestionTurn q => new() { Role = Role.User, Content = AskedAt(q) },
        ModelTurn m => new() { Role = Role.Assistant, Content = StoredBlocks.ToParams(m.Blocks) },
        ToolResultsTurn r => new() { Role = Role.User, Content = r.Results.Select(ToResult).ToList() },
        _ => throw new ArgumentOutOfRangeException(nameof(turn), turn.GetType().Name, "Unknown turn type."),
    };

    // Invariant: a custom format's ":" is the culture's time separator, which is "." in some cultures.
    private static string AskedAt(UserQuestionTurn question) =>
        $"Asked at {question.AskedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}Z.\n\n{question.Text}";

    private static BetaContentBlockParam ToResult(AssistantToolResult result) => new BetaToolResultBlockParam
    {
        ToolUseID = result.ToolCallId,
        Content = result.Content,
        IsError = result.IsError,
    };
}
