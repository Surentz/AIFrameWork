namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// Which stored turns carry an answer, and so may be rated: a quick reply's own turn, and a model
/// turn that ends its question — the last turn of the conversation, or one followed by the next
/// question. A model turn followed by tool results is a step on the way, never an answer.
/// </summary>
internal static class AnswerTurns
{
    public static IReadOnlySet<int> Of(IEnumerable<(int Seq, string Kind)> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var ordered = turns.OrderBy(static t => t.Seq).ToList();
        var answers = new SortedSet<int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (seq, kind) = ordered[i];
            var next = i + 1 < ordered.Count ? ordered[i + 1].Kind : null;
            if (kind is AssistantTurnCodec.Quick or AssistantTurnCodec.Suggested
                || (kind is AssistantTurnCodec.Model && next is null or AssistantTurnCodec.Question))
            {
                answers.Add(seq);
            }
        }

        return answers;
    }
}
