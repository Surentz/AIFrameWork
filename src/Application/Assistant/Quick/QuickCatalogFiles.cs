using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AiFramework.Application.Assistant.Quick;

public sealed record QuickPhrase(string IntentId, string Text);

/// <summary>One section of a help article: what the answer quotes, and the article it belongs to.</summary>
public sealed record HelpSection(string ArticleId, string Title, string Question, string Heading, string Text, QuickLink Link);

public sealed record QuickCatalog(IReadOnlyList<QuickPhrase> Phrases, IReadOnlyList<HelpSection> Help);

/// <summary>
/// Reads the catalog embedded in this assembly: Catalog/en/phrases.json and Catalog/en/help/*.md.
/// Data, not code, so a phrase or an article changes in review like any other file, and the
/// evaluation gate checks the result. ADR 0036.
/// </summary>
public static class QuickCatalogFiles
{
    private const string Prefix = "quick/en/";

    public static QuickCatalog Load()
    {
        var assembly = typeof(QuickCatalogFiles).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Select(n => (Resource: n, Path: n.Replace('\\', '/')))
            .Where(n => n.Path.StartsWith(Prefix, StringComparison.Ordinal))
            .ToList();

        var phrasesFile = names.Single(n => string.Equals(n.Path, Prefix + "phrases.json", StringComparison.Ordinal));
        var phrases = ParsePhrases(Read(assembly, phrasesFile.Resource));

        var help = names
            .Where(n => n.Path.StartsWith(Prefix + "help/", StringComparison.Ordinal) && n.Path.EndsWith(".md", StringComparison.Ordinal))
            .OrderBy(n => n.Path, StringComparer.Ordinal)
            .SelectMany(n => ParseHelp(Read(assembly, n.Resource)))
            .ToList();

        return new QuickCatalog(phrases, help);
    }

    /// <summary>
    /// Parses one help article. A malformed article is refused rather than skipped: an unclosed front
    /// matter, text outside a section, a missing required field, or no sections at all each throw.
    /// </summary>
    public static IReadOnlyList<HelpSection> ParseHelp(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            throw new FormatException("A help article starts with a --- front-matter block.");
        }

        var (front, bodyStart) = ReadFrontMatter(lines);

        // Every field is checked here, before any section, so an article with no ## sections still fails.
        var id = Required(front, "id");
        var title = Required(front, "title");
        var question = Required(front, "question");
        var link = new QuickLink(Required(front, "linkLabel"), Required(front, "link"));

        var sections = ReadSections(lines, bodyStart)
            .Select(s => new HelpSection(id, title, question, s.Heading, s.Text, link))
            .ToList();
        if (sections.Count == 0)
        {
            throw new FormatException("A help article needs at least one ## section.");
        }

        return sections;
    }

    /// <summary>Reads the --- block at the top; returns its key/value pairs and the index of the first body line.</summary>
    private static (Dictionary<string, string> Front, int BodyStart) ReadFrontMatter(string[] lines)
    {
        var front = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 1;
        for (; i < lines.Length && !string.Equals(lines[i].Trim(), "---", StringComparison.Ordinal); i++)
        {
            var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                front[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        if (i == lines.Length)
        {
            throw new FormatException("A help article's front-matter block is never closed with ---.");
        }

        return (front, i + 1);
    }

    private static string Required(Dictionary<string, string> front, string name) =>
        front.TryGetValue(name, out var value) && value.Length > 0
            ? value
            : throw new FormatException($"A help article's front matter needs '{name}'.");

    /// <summary>Splits the body at its ## headings. Text before the first heading belongs to no section, so it is refused.</summary>
    private static List<(string Heading, string Text)> ReadSections(string[] lines, int bodyStart)
    {
        var sections = new List<(string Heading, string Text)>();
        string? heading = null;
        var text = new StringBuilder();

        void Close()
        {
            if (heading is not null)
            {
                sections.Add((heading, text.ToString().Trim()));
            }

            text.Clear();
        }

        for (var i = bodyStart; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                heading = line[3..].Trim();
            }
            else if (line.Length > 0)
            {
                if (heading is null)
                {
                    throw new FormatException("A help article's text before the first ## heading is not part of any section.");
                }

                text.Append(text.Length > 0 ? " " : string.Empty).Append(line);
            }
        }

        Close();
        return sections;
    }

    private static List<QuickPhrase> ParsePhrases(string json)
    {
        using var document = JsonDocument.Parse(json);
        return
        [
            .. document.RootElement.GetProperty("intents").EnumerateObject()
                .SelectMany(intent => intent.Value.EnumerateArray()
                    .Select(phrase => new QuickPhrase(intent.Name, phrase.GetString() ?? string.Empty))),
        ];
    }

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource '{resource}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
