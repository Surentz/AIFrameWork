namespace AiFramework.Application.Assistant;

/// <summary>
/// A conversation's name in the sidebar: its first question, never anything the model wrote.
/// Whitespace collapses to single spaces; a long question is cut at a word boundary with "…".
/// </summary>
public static class AssistantTitle
{
    public const int MaxLength = 60;

    private static readonly char[] Whitespace = [' ', '\t', '\r', '\n'];

    public static string From(string question)
    {
        ArgumentNullException.ThrowIfNull(question);

        var flat = string.Join(' ', question.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= MaxLength)
        {
            return flat;
        }

        // One character is kept for the ellipsis. A word boundary in the first half would throw
        // away too much, so then the cut is mid-word.
        var cut = flat[..(MaxLength - 1)];
        if (char.IsHighSurrogate(cut[^1]))
        {
            // Half an emoji is not valid UTF-8, and Npgsql refuses to write it.
            cut = cut[..^1];
        }

        var space = cut.LastIndexOf(' ');
        return (space > MaxLength / 2 ? cut[..space] : cut).TrimEnd() + "…";
    }
}
