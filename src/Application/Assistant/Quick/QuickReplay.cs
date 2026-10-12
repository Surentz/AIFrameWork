using System.Text;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// The plain text Claude is given for a quick reply when the user later asks the AI in the same
/// conversation: the answer's sentence, every figure, and the screen it linked to. Built once, when
/// the turn is written, and never again. ADR 0036.
/// </summary>
public static class QuickReplay
{
    public static string For(QuickAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var text = new StringBuilder(answer.Text);
        foreach (var fact in answer.Facts)
        {
            text.Append('\n').Append(fact.Label).Append(": ").Append(fact.Value);
        }

        if (answer.Table is { } table)
        {
            foreach (var row in table.Rows)
            {
                text.Append('\n').AppendJoin("; ", table.Columns.Zip(row, (column, value) => $"{column}: {value}"));
            }
        }

        if (answer.Link is { } link)
        {
            text.Append("\n(Linked: ").Append(link.Label).Append(", ").Append(link.To).Append(')');
        }

        return text.ToString();
    }

    public static string For(QuickSuggestions suggestions)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        return suggestions.Options.Count == 0
            ? suggestions.Text
            : $"{suggestions.Text} Suggested: {string.Join("; ", suggestions.Options)}";
    }

    public static AssistantTurn Turn(QuickReply reply) => reply switch
    {
        QuickAnswer answer => new QuickAnswerTurn(answer, For(answer)),
        QuickSuggestions suggestions => new SuggestionsTurn(suggestions, For(suggestions)),
        _ => throw new ArgumentOutOfRangeException(nameof(reply), reply?.GetType().Name, "Unknown quick reply."),
    };
}
