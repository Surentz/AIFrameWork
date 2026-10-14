namespace AiFramework.Application.Assistant.Quick;

/// <summary>What the quick path says: an answer, or suggestions when it is not sure. ADR 0036.</summary>
#pragma warning disable S2094 // A closed hierarchy's base, as AssistantTurn: value equality on both reply types
public abstract record QuickReply;
#pragma warning restore S2094

public enum QuickAnswerKind
{
    /// <summary>Figures from a query, for the caller.</summary>
    Instant,

    /// <summary>A help article's section, quoted.</summary>
    Help,
}

public sealed record QuickFact(string Label, string Value);

public sealed record QuickTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>An in-app path, such as /orders/{id}. The page renders it only if it matches its allow-list.</summary>
public sealed record QuickLink(string Label, string To);

/// <summary>
/// One answer, already formatted: every figure in it came from a query. <see cref="Title"/> is the
/// help article's title; <see cref="AnsweredAs"/> is the corrected question when the spelling pass
/// made the match.
/// </summary>
public sealed record QuickAnswer(
    QuickAnswerKind Kind,
    string? Title,
    string Text,
    IReadOnlyList<QuickFact> Facts,
    QuickTable? Table,
    QuickLink? Link,
    string? AnsweredAs) : QuickReply;

/// <summary>Not sure: the closest canonical questions, each answered instantly when chosen.</summary>
public sealed record QuickSuggestions(string Text, IReadOnlyList<string> Options) : QuickReply;
