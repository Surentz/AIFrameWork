using System.Globalization;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;

namespace AiFramework.Application.Tests.Orders;

public sealed class OrderExportCsvTests
{
    private const string Header =
        "OrderId,Sku,Product,Quantity,UnitPrice,Total,Status,PlacedAt,ShippedAt,CancelledAt,CancellationReason";

    private static readonly Guid OrderId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DateTimeOffset PlacedAt = new(2026, 10, 3, 19, 5, 1, TimeSpan.Zero);

    private static OrderExportRow Row(
        string sku = "SKU-1",
        string? productName = "Widget",
        int quantity = 2,
        decimal? unitPrice = 9.95m,
        OrderStatus status = OrderStatus.Placed,
        DateTimeOffset? placedAt = null,
        DateTimeOffset? shippedAt = null,
        DateTimeOffset? cancelledAt = null,
        string? cancellationReason = null) =>
        new(OrderId, sku, productName, quantity, unitPrice, status, placedAt ?? PlacedAt,
            shippedAt, cancelledAt, cancellationReason);

    private static string[] Lines(string csv) => csv.Split("\r\n");

    [Fact]
    public void Build_WithNoRows_IsTheHeaderAlone()
    {
        var csv = OrderExportCsv.Build([]);

        csv.Should().Be(Header + "\r\n");
    }

    [Fact]
    public void Build_WritesOneCrlfTerminatedLinePerOrder()
    {
        var csv = OrderExportCsv.Build([Row(), Row(sku: "SKU-2")]);

        Lines(csv).Should().HaveCount(4, "header, two orders, and the empty remainder after the last CRLF");
        Lines(csv)[^1].Should().BeEmpty();
    }

    [Fact]
    public void Build_WritesEveryColumnOfAPlacedOrder()
    {
        var csv = OrderExportCsv.Build([Row()]);

        Lines(csv)[1].Should().Be(
            "0f8fad5b-d9cb-469f-a165-70867728950e,SKU-1,Widget,2,9.95,19.90,Placed,2026-10-03T19:05:01Z,,,");
    }

    [Fact]
    public void Build_WritesTheShippedAndCancelledColumns()
    {
        var shipped = Row(status: OrderStatus.Shipped, shippedAt: PlacedAt.AddDays(1));
        var cancelled = Row(
            status: OrderStatus.Cancelled, cancelledAt: PlacedAt.AddHours(2), cancellationReason: "Changed my mind");

        var lines = Lines(OrderExportCsv.Build([shipped, cancelled]));

        lines[1].Should().EndWith(",Shipped,2026-10-03T19:05:01Z,2026-10-04T19:05:01Z,,");
        lines[2].Should().EndWith(",Cancelled,2026-10-03T19:05:01Z,,2026-10-03T21:05:01Z,Changed my mind");
    }

    [Fact]
    public void Build_WritesTimestampsInUtcWhateverTheirOffset()
    {
        var csv = OrderExportCsv.Build([Row(placedAt: new DateTimeOffset(2026, 10, 3, 21, 5, 1, TimeSpan.FromHours(2)))]);

        Lines(csv)[1].Should().Contain(",2026-10-03T19:05:01Z,");
    }

    [Fact]
    public void Build_LeavesPriceAndTotalEmptyWhenThePriceIsUnknown()
    {
        var csv = OrderExportCsv.Build([Row(productName: null, unitPrice: null)]);

        Lines(csv)[1].Should().Be("0f8fad5b-d9cb-469f-a165-70867728950e,SKU-1,,2,,,Placed,2026-10-03T19:05:01Z,,,");
    }

    [Fact]
    public void Build_WritesDecimalsWithAPointWhateverTheCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("da-DK");
        try
        {
            var csv = OrderExportCsv.Build([Row(unitPrice: 1234.5m, quantity: 3)]);

            Lines(csv)[1].Should().Contain(",1234.50,3703.50,");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("two\r\nlines", "\"two\r\nlines\"")]
    public void Build_QuotesAFieldThatNeedsIt(string reason, string expected)
    {
        var csv = OrderExportCsv.Build([Row(status: OrderStatus.Cancelled, cancelledAt: PlacedAt, cancellationReason: reason)]);

        csv.Should().Contain("," + expected + "\r\n");
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tcmd")]
    [InlineData("\rcmd")]
    public void Build_DefusesAUserTypedValueASpreadsheetWouldEvaluate(string value)
    {
        var csv = OrderExportCsv.Build([Row(productName: value)]);

        csv.Should().Contain("'" + value);
    }

    [Fact]
    public void Build_DefusesTheSkuAndTheReasonAsWellAsTheProduct()
    {
        var csv = OrderExportCsv.Build([
            Row(sku: "=SKU", status: OrderStatus.Cancelled, cancelledAt: PlacedAt, cancellationReason: "@me"),
        ]);

        Lines(csv)[1].Should().Contain(",'=SKU,").And.EndWith(",'@me");
    }

    [Fact]
    public void Build_LeavesAnOrdinaryHyphenatedValueAlone()
    {
        var csv = OrderExportCsv.Build([Row(sku: "SKU-1", productName: "Blue-green widget")]);

        Lines(csv)[1].Should().Contain(",SKU-1,Blue-green widget,");
    }
}
