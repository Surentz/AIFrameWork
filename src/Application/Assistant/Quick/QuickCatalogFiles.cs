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

    public static IReadOnlyList<HelpSection> ParseHelp(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            throw new FormatException("A help article starts with a --- front-matter block.");
        }

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

        string Field(string name) => front.TryGetValue(name, out var value) && value.Length > 0
            ? value
            : throw new FormatException($"A help article's front matter needs '{name}'.");

        var link = new QuickLink(Field("linkLabel"), Field("link"));
        var sections = new List<HelpSection>();
        string? heading = null;
        var text = new StringBuilder();

        void Close()
        {
            if (heading is not null)
            {
                sections.Add(new HelpSection(Field("id"), Field("title"), Field("question"), heading, text.ToString().Trim(), link));
            }

            text.Clear();
        }

        for (i++; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                heading = line[3..].Trim();
            }
            else if (line.Length > 0)
            {
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
