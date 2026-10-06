using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace AiFramework.Infrastructure.Exports;

/// <summary>
/// Lays out an <see cref="OrderExportReport"/> as an A4 landscape PDF for sharing: a brand band,
/// three summary cards, and a table whose header repeats on every page. Static text and shapes only
/// — no links, scripts, attachments or form fields — so nothing a user typed can become active
/// content. ADR 0030.
/// </summary>
internal sealed class MigraDocOrderExportRenderer : IOrderExportRenderer
{
    // A4 landscape is 297 mm wide; 15 mm margins leave 267 mm of content.
    private const double ContentWidthMm = 267;
    private static readonly double[] ColumnsMm = [28, 24, 95, 14, 28, 30, 48];
    private static readonly string[] Headings = ["PLACED", "ORDER", "PRODUCT", "QTY", "UNIT PRICE", "TOTAL", "STATUS"];

    public MigraDocOrderExportRenderer() => NotoSansFontResolver.Install();

    public byte[] Render(OrderExportReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = new Document();
        document.Info.Title = "Order history";
        document.Info.Author = report.OwnerName;
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = NotoSansFontResolver.FamilyName;
        normal.Font.Size = 9;
        normal.Font.Color = OrderExportPalette.Text.ToColor();

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.LeftMargin = Unit.FromMillimeter(15);
        section.PageSetup.RightMargin = Unit.FromMillimeter(15);
        section.PageSetup.TopMargin = Unit.FromMillimeter(15);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(20);
        section.PageSetup.FooterDistance = Unit.FromMillimeter(8);

        Footer(section, report);
        Band(section, report);
        Summary(section, report);
        if (!report.IsEmpty)
        {
            Orders(section, report);
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Info.Creator = "AIFrameWork";
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output);
        return output.ToArray();
    }

    private static void Footer(Section section, OrderExportReport report)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Borders.Top.Color = OrderExportPalette.Hairline.ToColor();
        footer.Format.Borders.Top.Width = Unit.FromPoint(0.5);
        footer.Format.Borders.DistanceFromTop = Unit.FromMillimeter(2);
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        footer.AddText($"Order history · {report.OwnerName} · Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();
    }

    private static void Band(Section section, OrderExportReport report)
    {
        var band = section.AddTable();
        band.AddColumn(Unit.FromMillimeter(ContentWidthMm));
        var row = band.AddRow();
        row.Shading.Color = OrderExportPalette.Brand.ToColor();
        row.TopPadding = Unit.FromMillimeter(6);
        row.BottomPadding = Unit.FromMillimeter(6);
        var cell = row.Cells[0];
        cell.Format.LeftIndent = Unit.FromMillimeter(4);

        var title = cell.AddParagraph("Order history");
        title.Format.Font.Size = 22;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = OrderExportPalette.OnBrand.ToColor();

        var subtitle = cell.AddParagraph($"{report.OwnerName} · {report.Generated}");
        subtitle.Format.Font.Size = 10;
        subtitle.Format.SpaceBefore = Unit.FromMillimeter(1);
        subtitle.Format.Font.Color = OrderExportPalette.OnBrandSoft.ToColor();
    }

    private static void Summary(Section section, OrderExportReport report)
    {
        var cards = section.AddTable();
        cards.TopPadding = Unit.FromMillimeter(3);
        cards.BottomPadding = Unit.FromMillimeter(3);
        cards.LeftPadding = Unit.FromMillimeter(4);

        if (report.IsEmpty)
        {
            cards.AddColumn(Unit.FromMillimeter(ContentWidthMm));
            var only = cards.AddRow();
            only.Shading.Color = OrderExportPalette.Card.ToColor();
            Value(only.Cells[0], "No orders yet.");
            Spacer(section);
            return;
        }

        // card, gap, card, gap, card: 3 × 85 mm + 2 × 6 mm = 267 mm.
        foreach (var width in new[] { 85.0, 6, 85, 6, 85 })
        {
            cards.AddColumn(Unit.FromMillimeter(width));
        }

        var row = cards.AddRow();
        Card(row.Cells[0], "ORDERS", report.OrderCount.ToString(System.Globalization.CultureInfo.InvariantCulture), report.Period);
        Card(row.Cells[2], "TOTAL VALUE", report.TotalValue, report.TotalValueNote);
        Card(row.Cells[4], "BY STATUS", report.StatusSummary, null);
        Spacer(section);
    }

    private static void Card(Cell cell, string label, string value, string? note)
    {
        cell.Shading.Color = OrderExportPalette.Card.ToColor();
        var heading = cell.AddParagraph(label);
        heading.Format.Font.Size = 7.5;
        heading.Format.Font.Bold = true;
        heading.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        Value(cell, value);
        if (note is not null)
        {
            var small = cell.AddParagraph(note);
            small.Format.Font.Size = 8;
            small.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        }
    }

    private static void Value(Cell cell, string value)
    {
        var big = cell.AddParagraph(value);
        big.Format.Font.Size = 15;
        big.Format.Font.Bold = true;
        big.Format.SpaceBefore = Unit.FromMillimeter(1);
    }

    private static void Spacer(Section section) =>
        section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(4);

    private static void Orders(Section section, OrderExportReport report)
    {
        var table = section.AddTable();
        table.Borders.Bottom.Color = OrderExportPalette.Hairline.ToColor();
        table.Borders.Bottom.Width = Unit.FromPoint(0.5);
        table.TopPadding = Unit.FromMillimeter(1.8);
        table.BottomPadding = Unit.FromMillimeter(1.8);
        foreach (var width in ColumnsMm)
        {
            table.AddColumn(Unit.FromMillimeter(width));
        }

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = OrderExportPalette.Sunken.ToColor();
        for (var i = 0; i < Headings.Length; i++)
        {
            var p = head.Cells[i].AddParagraph(Headings[i]);
            p.Format.Font.Size = 7.5;
            p.Format.Font.Bold = true;
            p.Format.Font.Color = OrderExportPalette.Muted.ToColor();
            if (i is 3 or 4 or 5)
            {
                p.Format.Alignment = ParagraphAlignment.Right;
            }
        }

        for (var n = 0; n < report.Rows.Count; n++)
        {
            OrderRow(table, report.Rows[n], zebra: n % 2 == 1);
        }

        var total = table.AddRow();
        total.Borders.Top.Color = OrderExportPalette.Rule.ToColor();
        total.Borders.Top.Width = Unit.FromPoint(1);
        var label = total.Cells[0].AddParagraph($"Total value ({report.TotalValueNote})");
        total.Cells[0].MergeRight = 4;
        label.Format.Alignment = ParagraphAlignment.Right;
        label.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        var sum = total.Cells[5].AddParagraph(report.TotalValue);
        sum.Format.Alignment = ParagraphAlignment.Right;
        sum.Format.Font.Bold = true;
    }

    private static void OrderRow(Table table, OrderExportReportRow order, bool zebra)
    {
        var row = table.AddRow();
        row.VerticalAlignment = VerticalAlignment.Top;
        if (zebra)
        {
            row.Shading.Color = OrderExportPalette.Sunken.ToColor();
        }

        row.Cells[0].AddParagraph(order.Placed);
        row.Cells[1].AddParagraph(order.Reference).Format.Font.Color = OrderExportPalette.Muted.ToColor();
        row.Cells[2].AddParagraph(order.Product);
        if (order.Sku is not null)
        {
            var sku = row.Cells[2].AddParagraph(order.Sku);
            sku.Format.Font.Size = 7.5;
            sku.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        }

        Number(row.Cells[3], order.Quantity);
        Number(row.Cells[4], order.UnitPrice);
        Number(row.Cells[5], order.Total);
        Status(row.Cells[6], order);
    }

    private static void Number(Cell cell, string text) =>
        cell.AddParagraph(text).Format.Alignment = ParagraphAlignment.Right;

    private static void Status(Cell cell, OrderExportReportRow order)
    {
        var (text, background) = order.Status switch
        {
            OrderStatus.Shipped => (OrderExportPalette.ShippedText, OrderExportPalette.ShippedBackground),
            OrderStatus.Cancelled => (OrderExportPalette.CancelledText, OrderExportPalette.CancelledBackground),
            _ => (OrderExportPalette.PlacedText, OrderExportPalette.PlacedBackground),
        };

        // A rectangular badge: MigraDoc draws no rounded corners. The right indent keeps it about
        // as wide as its word rather than the whole column.
        var badge = cell.AddParagraph(order.Status.ToString());
        badge.Format.Shading.Color = background.ToColor();
        badge.Format.Font.Color = text.ToColor();
        badge.Format.Font.Bold = true;
        badge.Format.Font.Size = 8;
        badge.Format.LeftIndent = Unit.FromMillimeter(0.5);
        badge.Format.RightIndent = Unit.FromMillimeter(24);
        badge.Format.Alignment = ParagraphAlignment.Center;

        if (order.StatusDetail is not null)
        {
            Secondary(cell, order.StatusDetail, italic: false);
        }

        if (order.CancellationReason is not null)
        {
            Secondary(cell, order.CancellationReason, italic: true);
        }
    }

    private static void Secondary(Cell cell, string text, bool italic)
    {
        var p = cell.AddParagraph(text);
        p.Format.Font.Size = 7.5;
        p.Format.Font.Italic = italic;
        p.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        p.Format.SpaceBefore = Unit.FromMillimeter(0.8);
    }
}
