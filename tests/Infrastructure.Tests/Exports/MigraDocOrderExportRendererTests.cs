using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Exports;
using FluentAssertions;
using UglyToad.PdfPig;

namespace AiFramework.Infrastructure.Tests.Exports;

/// <summary>
/// The real renderer, read back with PdfPig. Text assertions go through extracted words, which is
/// also the proof that the embedded font draws the characters (a missing glyph extracts as nothing).
/// </summary>
public sealed class MigraDocOrderExportRendererTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 18, 4, 0, TimeSpan.Zero);

    private static OrderExportRow Row(int n, string? name = null, OrderStatus status = OrderStatus.Placed, string? reason = null) =>
        new(Guid.NewGuid(), $"SKU-{n}", name ?? $"Widget {n}", 2, 10m, status, At.AddDays(-n), null,
            status == OrderStatus.Cancelled ? At : null, reason);

    private static byte[] Render(IReadOnlyCollection<OrderExportRow> rows, string owner = "Jane Doe") =>
        new MigraDocOrderExportRenderer().Render(OrderExportReport.Create(rows, owner, At));

    private static List<string> PageTexts(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return [.. document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))];
    }

    [Fact]
    public void Render_ProducesAPdf()
    {
        var pdf = Render([Row(1)]);

        pdf.Take(5).Should().Equal("%PDF-"u8.ToArray());
    }

    [Fact]
    public void Render_SaysWhatItIsAndWhoseItIs()
    {
        var text = PageTexts(Render([Row(1)])).Single();

        text.Should().Contain("Order history").And.Contain("Jane Doe").And.Contain("Generated 6 Oct 2026, 18:04 UTC");
    }

    [Fact]
    public void Render_ListsEveryOrdersSkuAndTheTotal()
    {
        var text = PageTexts(Render([Row(1), Row(2)])).Single();

        text.Should().Contain("SKU-1").And.Contain("SKU-2").And.Contain("40.00");
    }

    [Fact]
    public void Render_ALongExport_NumbersEveryPageAndRepeatsTheTableHeader()
    {
        var pages = PageTexts(Render([.. Enumerable.Range(1, 80).Select(n => Row(n))]));

        pages.Should().HaveCountGreaterThan(1);
        pages.Select((text, i) => (text, i)).Should().AllSatisfy(p =>
        {
            p.text.Should().Contain($"Page {p.i + 1} of {pages.Count}");
            p.text.Should().Contain("PRODUCT");
        });
    }

    [Theory]
    [InlineData("Smørrebrød")]
    [InlineData("Ελληνικά")]
    [InlineData("Борщ")]
    public void Render_DrawsNonAsciiProductNamesAsText(string name)
    {
        PageTexts(Render([Row(1, name)])).Single().Should().Contain(name);
    }

    [Fact]
    public void Render_ShowsACancelledOrdersReason()
    {
        var text = PageTexts(Render([Row(1, status: OrderStatus.Cancelled, reason: "Changed my mind")])).Single();

        text.Should().Contain("Cancelled").And.Contain("Changed my mind");
    }

    [Fact]
    public void Render_WithNoOrders_SaysSo()
    {
        PageTexts(Render([])).Single().Should().Contain("No orders yet.");
    }

    // Review Focus 3: text the font cannot draw, and text longer than its cell, must not throw.
    [Fact]
    public void Render_WithCharactersTheFontLacksAndVeryLongText_StillRenders()
    {
        var longName = string.Concat(Enumerable.Repeat("Extraordinarily long product name ", 20));
        var rows = new[]
        {
            Row(1, "注文の品"),
            Row(2, longName, OrderStatus.Cancelled, reason: new string('x', 600)),
        };

        var pdf = Render(rows);

        PageTexts(pdf).Should().NotBeEmpty();
    }

    // The renderer is a singleton and PDFsharp's font cache is process-wide, while the heavy lane runs
    // builds side by side (Jobs__HeavyParallelism, 2 per pod by default). Eight at once, each with its
    // own owner, must each come out whole and with its own text.
    [Fact]
    public async Task Render_CalledConcurrently_ProducesEveryDocumentIntact()
    {
        var renderer = new MigraDocOrderExportRenderer();
        var owners = Enumerable.Range(1, 8).Select(n => $"Owner {n}").ToArray();

        var pdfs = await Task.WhenAll(owners.Select(owner => Task.Run(() =>
            renderer.Render(OrderExportReport.Create([.. Enumerable.Range(1, 30).Select(n => Row(n))], owner, At)))));

        pdfs.Select((pdf, i) => PageTexts(pdf)[0].Contains(owners[i], StringComparison.Ordinal))
            .Should().AllBeEquivalentTo(true);
    }

    [Fact]
    public void Render_TitlesTheDocumentAndNamesItsAuthor()
    {
        using var document = PdfDocument.Open(Render([Row(1)]));

        document.Information.Title.Should().Be("Order history");
        document.Information.Author.Should().Be("Jane Doe");
        document.Information.Creator.Should().Be("AIFrameWork");
    }
}
