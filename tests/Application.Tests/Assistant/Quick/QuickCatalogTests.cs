using System.Text.RegularExpressions;
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class QuickCatalogTests
{
    private static readonly QuickCatalog Catalog = QuickCatalogFiles.Load();

    /// <summary>The screens a link may open. Kept in step with frontend/src/features/assistant/quickLinks.ts.</summary>
    private static readonly Regex[] Routes =
    [
        Route("^/orders$"), Route("^/orders/new$"), Route("^/orders/exports$"), Route("^/orders/[0-9a-f-]{36}$"),
        Route("^/products$"), Route("^/products/[0-9a-f-]{36}$"), Route("^/statistics/population$"),
        Route("^/monitoring(/(jobs|traffic|integrations|logins|users))?$"), Route("^/account/password$"), Route("^/notifications$"),
    ];

    /// <summary>A match timeout, as the repo's own GeneratedRegex patterns carry (MA0009).</summary>
    private static Regex Route(string pattern) => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The five required front-matter fields, one per line, with values.</summary>
    private static readonly string[] SampleFrontMatter =
    [
        "id: sample", "title: A sample", "question: How do I sample?", "link: /orders", "linkLabel: All orders",
    ];

    /// <summary>The six articles shipped in Catalog/en/help, sorted by id.</summary>
    private static readonly string[] HelpArticleIds =
    [
        "catalogue", "change-password", "export-orders", "notifications", "place-order", "sign-out-everywhere",
    ];

    [Fact]
    public void EveryIntent_HasAtLeastTenPhrases_IncludingItsCanonicalQuestion()
    {
        foreach (var intent in QuickIntents.All)
        {
            var phrases = Catalog.Phrases.Where(p => string.Equals(p.IntentId, intent.Id, StringComparison.Ordinal)).Select(p => p.Text).ToList();

            phrases.Should().HaveCountGreaterThanOrEqualTo(10, intent.Id);
            phrases.Should().Contain(intent.CanonicalQuestion, "a suggestion chip must always be answered instantly");
        }
    }

    [Fact]
    public void EveryPhrase_BelongsToAKnownIntent()
    {
        Catalog.Phrases.Select(p => p.IntentId).Distinct(StringComparer.Ordinal)
            .Should().BeSubsetOf(QuickIntents.All.Select(i => i.Id));
    }

    [Fact]
    public void NoPhrase_AppearsUnderTwoIntents()
    {
        Catalog.Phrases
            .GroupBy(p => p.Text.Trim().ToUpperInvariant(), StringComparer.Ordinal)
            .Where(g => g.Select(p => p.IntentId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .Should().BeEmpty();
    }

    [Fact]
    public void IntentIds_AreUnique()
    {
        QuickIntents.All.Select(i => i.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryHelpArticle_HasALinkToARealScreen_AndAtLeastOneSection()
    {
        Catalog.Help.Should().NotBeEmpty();
        foreach (var article in Catalog.Help.GroupBy(s => s.ArticleId, StringComparer.Ordinal))
        {
            Routes.Should().Contain(r => r.IsMatch(article.First().Link.To), article.Key);
            article.Should().OnlyContain(s => s.Text.Length > 20 && s.Question.EndsWith('?'));
        }
    }

    [Fact]
    public void ParseHelp_ReadsTheFrontMatterAndOneSectionPerHeading()
    {
        const string markdown = """
            ---
            id: sample
            title: A sample
            question: How do I sample?
            link: /orders
            linkLabel: All orders
            ---
            ## First
            The first section's text, which is long enough.

            ## Second
            The second section's text,
            over two lines.
            """;

        var sections = QuickCatalogFiles.ParseHelp(markdown);

        sections.Should().HaveCount(2);
        sections[0].Should().Be(new HelpSection(
            "sample", "A sample", "How do I sample?", "First", "The first section's text, which is long enough.", new QuickLink("All orders", "/orders")));
        sections[1].Text.Should().Be("The second section's text, over two lines.");
    }

    [Fact]
    public void ParseHelp_ThrowsFormatException_WhenTheFrontMatterIsNeverClosed()
    {
        const string markdown = """
            ---
            id: sample
            title: A sample
            question: How do I sample?
            link: /orders
            linkLabel: All orders
            ## First
            The first section's text, which is long enough.
            """;

        Action parse = () => QuickCatalogFiles.ParseHelp(markdown);

        parse.Should().Throw<FormatException>().WithMessage("*never closed*");
    }

    [Fact]
    public void ParseHelp_ThrowsFormatException_WhenTextComesBeforeTheFirstHeading()
    {
        const string markdown = """
            ---
            id: sample
            title: A sample
            question: How do I sample?
            link: /orders
            linkLabel: All orders
            ---
            A stray sentence that belongs to no section.
            ## First
            The first section's text, which is long enough.
            """;

        Action parse = () => QuickCatalogFiles.ParseHelp(markdown);

        parse.Should().Throw<FormatException>().WithMessage("*before the first*");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("title")]
    [InlineData("question")]
    [InlineData("link")]
    [InlineData("linkLabel")]
    public void ParseHelp_ThrowsFormatException_WhenARequiredFieldIsMissing(string field)
    {
        var frontMatter = SampleFrontMatter.Where(line => !line.StartsWith(field + ":", StringComparison.Ordinal));
        var markdown = $"---\n{string.Join("\n", frontMatter)}\n---\n";

        Action parse = () => QuickCatalogFiles.ParseHelp(markdown);

        parse.Should().Throw<FormatException>().WithMessage($"*'{field}'*");
    }

    [Fact]
    public void ParseHelp_ThrowsFormatException_WhenThereAreNoSections()
    {
        const string markdown = """
            ---
            id: sample
            title: A sample
            question: How do I sample?
            link: /orders
            linkLabel: All orders
            ---
            """;

        Action parse = () => QuickCatalogFiles.ParseHelp(markdown);

        parse.Should().Throw<FormatException>().WithMessage("*at least one*");
    }

    [Fact]
    public void ParseHelp_ReadsCrlfTheSameAsLf()
    {
        const string markdown = """
            ---
            id: sample
            title: A sample
            question: How do I sample?
            link: /orders
            linkLabel: All orders
            ---
            ## First
            The first section's text, which is long enough.

            ## Second
            The second section's text,
            over two lines.
            """;

        var fromCrlf = QuickCatalogFiles.ParseHelp(markdown.Replace("\n", "\r\n", StringComparison.Ordinal));

        fromCrlf.Should().Equal(QuickCatalogFiles.ParseHelp(markdown));
    }

    [Fact]
    public void Load_ReadsExactlyTheSixHelpArticles()
    {
        Catalog.Help.Select(s => s.ArticleId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Should().Equal(HelpArticleIds);
    }

    [Fact]
    public void Allows_GivesAMemberOnlyMemberIntents_AndAnOperatorAll()
    {
        var jobHealth = QuickIntents.Find(QuickIntents.JobHealth)!;
        var orders = QuickIntents.Find(QuickIntents.RecentOrders)!;

        QuickIntents.Allows(ToolScope.Member, jobHealth).Should().BeFalse();
        QuickIntents.Allows(ToolScope.Member, orders).Should().BeTrue();
        QuickIntents.Allows(ToolScope.Operator, jobHealth).Should().BeTrue();
    }
}
