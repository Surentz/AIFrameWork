using System.Text;
using System.Text.Json;
using AiFramework.Application.Assistant;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// Turns stored turns into what the page shows. A question is a user message; every model turn up
/// to the next question folds into one assistant message: its text blocks in order (joined as the
/// live stream joined them, with nothing between) and the names of the tools it called. Thinking,
/// tool inputs and tool results are not shown. Reads only — the turns are never changed.
/// </summary>
internal static class AssistantDisplay
{
    public static IReadOnlyList<AssistantDisplayMessage> Build(IEnumerable<AssistantTurnRow> turns)
    {
        var messages = new List<AssistantDisplayMessage>();
        StringBuilder? text = null;
        List<(string? Id, string Name)>? tools = null;

        foreach (var turn in turns)
        {
            switch (turn.Kind)
            {
                case AssistantTurnCodec.Question:
                    Flush(messages, ref text, ref tools);
                    messages.Add(new AssistantDisplayMessage(
                        AssistantDisplayMessage.UserRole, ((UserQuestionTurn)AssistantTurnCodec.Decode(turn)).Text, []));
                    break;

                case AssistantTurnCodec.Model:
                    text ??= new StringBuilder();
                    tools ??= [];
                    foreach (var block in ReadOrEmpty(turn.Payload))
                    {
                        if (block is { Type: "text", Text: { } t })
                        {
                            text.Append(t);
                        }
                        else if (block is { Type: "tool_use", Name: { } name })
                        {
                            tools.Add((block.Id, name));
                        }
                    }

                    break;

                case AssistantTurnCodec.ToolResults:
                    // The data the model read is not shown, but a refused call (a tool the question
                    // was not offered) is not a tool the assistant used, so its chip is dropped.
                    DropRefused(tools, turn);
                    break;

                default:
                    break;
            }
        }

        Flush(messages, ref text, ref tools);
        return messages;
    }

    private static void Flush(List<AssistantDisplayMessage> messages, ref StringBuilder? text, ref List<(string? Id, string Name)>? tools)
    {
        if (text is null || tools is null)
        {
            return;
        }

        messages.Add(new AssistantDisplayMessage(AssistantDisplayMessage.AssistantRole, text.ToString(), [.. tools.Select(static t => t.Name)]));
        text = null;
        tools = null;
    }

    private static void DropRefused(List<(string? Id, string Name)>? tools, AssistantTurnRow turn)
    {
        if (tools is null || AssistantTurnCodec.Decode(turn) is not ToolResultsTurn results)
        {
            return;
        }

        var refused = results.Results.Where(static r => r.IsError).Select(static r => r.ToolCallId).ToHashSet(StringComparer.Ordinal);
        tools.RemoveAll(t => t.Id is not null && refused.Contains(t.Id));
    }

    /// <summary>A turn whose blocks cannot be read is shown as nothing, never rewritten.</summary>
    private static IReadOnlyList<StoredBlock> ReadOrEmpty(string payload)
    {
        try
        {
            return StoredBlocks.Read(payload);
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
