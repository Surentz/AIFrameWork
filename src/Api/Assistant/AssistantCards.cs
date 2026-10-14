using AiFramework.Application.Assistant.Quick;

namespace AiFramework.Api.Assistant;

/// <summary>The one mapping from a quick reply to its wire shape, for the stream and for saved conversations.</summary>
public static class AssistantCards
{
    public static AssistantAnswerResponse ToResponse(QuickAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        return new AssistantAnswerResponse
        {
            Kind = answer.Kind,
            Title = answer.Title,
            Text = answer.Text,
            AnsweredAs = answer.AnsweredAs,
            Facts = [.. answer.Facts.Select(f => new AssistantFactResponse { Label = f.Label, Value = f.Value })],
            Table = answer.Table is { } table ? new AssistantTableResponse { Columns = table.Columns, Rows = table.Rows } : null,
            Link = answer.Link is { } link ? new AssistantLinkResponse { Label = link.Label, To = link.To } : null,
        };
    }

    public static AssistantSuggestionsResponse ToResponse(QuickSuggestions suggestions, bool aiAvailable)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        return new AssistantSuggestionsResponse { Text = suggestions.Text, Options = suggestions.Options, AiAvailable = aiAvailable };
    }
}
