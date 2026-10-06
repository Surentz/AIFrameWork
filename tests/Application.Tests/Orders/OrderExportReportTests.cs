using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;

namespace AiFramework.Application.Tests.Orders;

public sealed class OrderExportReportTests
{
    private static readonly DateTimeOffset Generated = new(2026, 10, 6, 18, 4, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset Jan12 = new(2026, 1, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Oct4 = new(2026, 10, 4, 23, 30, 0, TimeSpan.FromHours(-2));

    private static OrderExportRow Row(
        DateTimeOffset placedAt,
        OrderStatus status = OrderStatus.Placed,
        decimal? unitPrice = 10m,
        int quantity = 1,
        string? productName = "Widget",
        string sku = "SKU-1",
        Guid? id = null,
        DateTimeOffset? shippedAt = null,
        DateTimeOffset? cancelledAt = null,
        string? reason = null) =>
        new(id ?? Guid.NewGuid(), sku, productName, quantity, unitPrice, status, placedAt, shippedAt, cancelledAt, reason);

    private static OrderExportReport Report(params OrderExportRow[] rows) =>
        OrderExportReport.Create(rows, "Jane Doe", Generated);

    [Fact]
    public void Create_SaysWhoAndWhenInUtc()
    {
        var report = Report();

        report.OwnerName.Should().Be("Jane Doe");
        report.Generated.Should().Be("Generated 6 Oct 2026, 18:04 UTC");
    }

    [Fact]
    public void Create_WithNoOrders_IsEmptyWithNoPeriod()
    {
        var report = Report();

        report.IsEmpty.Should().BeTrue();
        report.OrderCount.Should().Be(0);
        report.Period.Should().BeNull();
        report.TotalValue.Should().Be("0.00");
    }

    [Fact]
    public void Create_ListsTheNewestOrderFirst()
    {
        var older = Row(Jan12, sku: "OLD");
        var newer = Row(Oct4, sku: "NEW");

        var report = Report(older, newer);

        report.Rows.Select(r => r.Sku).Should().Equal("NEW", "OLD");
    }

    [Fact]
    public void Create_GivesThePeriodFromFirstToLastOrderInUtc()
    {
        // Oct4 is 4 Oct 23:30 at -02:00, which is 5 Oct in UTC.
        Report(Row(Oct4), Row(Jan12)).Period.Should().Be("12 Jan 2026 – 5 Oct 2026");
    }

    [Fact]
    public void Create_WithOrdersOnOneDay_GivesThatDayOnce()
    {
        Report(Row(Jan12), Row(Jan12.AddHours(3))).Period.Should().Be("12 Jan 2026");
    }

    [Fact]
    public void Create_TotalsQuantityTimesPriceAndExcludesCancelled()
    {
        var report = Report(
            Row(Jan12, unitPrice: 1234.5m, quantity: 2),
            Row(Jan12, status: OrderStatus.Shipped, unitPrice: 0.25m, quantity: 4),
            Row(Jan12, status: OrderStatus.Cancelled, unitPrice: 999m));

        report.TotalValue.Should().Be("2,470.00");
        report.TotalValueNote.Should().Be("excludes cancelled orders");
    }

    [Fact]
    public void Create_ExcludesOrdersWithNoRecordedPriceAndSaysHowMany()
    {
        var report = Report(Row(Jan12, unitPrice: 5m), Row(Jan12, unitPrice: null), Row(Jan12, unitPrice: null));

        report.TotalValue.Should().Be("5.00");
        report.TotalValueNote.Should().Be("excludes cancelled orders and 2 without a recorded price");
    }

    [Fact]
    public void Create_WhenEveryOrderIsCancelledOrUnpriced_TotalsZero()
    {
        var report = Report(Row(Jan12, status: OrderStatus.Cancelled), Row(Jan12, unitPrice: null));

        report.TotalValue.Should().Be("0.00");
    }

    [Fact]
    public void Create_CountsEachStatus()
    {
        var report = Report(
            Row(Jan12), Row(Jan12),
            Row(Jan12, status: OrderStatus.Shipped),
            Row(Jan12, status: OrderStatus.Cancelled));

        report.OrderCount.Should().Be(4);
        report.StatusSummary.Should().Be("Placed 2 · Shipped 1 · Cancelled 1");
    }

    [Fact]
    public void Create_ShortensTheOrderIdToEightUpperCaseCharacters()
    {
        var row = Row(Jan12, id: Guid.Parse("3f2a9c01-aaaa-bbbb-cccc-ddddeeeeffff"));

        Report(row).Rows.Single().Reference.Should().Be("3F2A9C01");
    }

    [Fact]
    public void Create_FormatsARowsDateQuantityAndMoney()
    {
        var row = Report(Row(Oct4, unitPrice: 1234.5m, quantity: 2)).Rows.Single();

        row.Placed.Should().Be("5 Oct 2026");
        row.Quantity.Should().Be("2");
        row.UnitPrice.Should().Be("1,234.50");
        row.Total.Should().Be("2,469.00");
    }

    [Fact]
    public void Create_ShowsADashForAMissingPrice()
    {
        var row = Report(Row(Jan12, unitPrice: null)).Rows.Single();

        row.UnitPrice.Should().Be("–");
        row.Total.Should().Be("–");
    }

    [Fact]
    public void Create_PutsTheSkuUnderTheProductName()
    {
        var row = Report(Row(Jan12, productName: "Smørrebrød", sku: "SKU-9")).Rows.Single();

        row.Product.Should().Be("Smørrebrød");
        row.Sku.Should().Be("SKU-9");
    }

    [Fact]
    public void Create_WithNoProductName_ShowsTheSkuAlone()
    {
        var row = Report(Row(Jan12, productName: null, sku: "SKU-9")).Rows.Single();

        row.Product.Should().Be("SKU-9");
        row.Sku.Should().BeNull();
    }

    [Fact]
    public void Create_SaysWhenAnOrderShipped()
    {
        var row = Report(Row(Jan12, status: OrderStatus.Shipped, shippedAt: new DateTimeOffset(2026, 3, 3, 8, 0, 0, TimeSpan.Zero)))
            .Rows.Single();

        row.StatusDetail.Should().Be("Shipped 3 Mar 2026");
        row.CancellationReason.Should().BeNull();
    }

    [Fact]
    public void Create_SaysWhenAndWhyAnOrderWasCancelled()
    {
        var row = Report(Row(
                Jan12,
                status: OrderStatus.Cancelled,
                cancelledAt: new DateTimeOffset(2026, 3, 5, 8, 0, 0, TimeSpan.Zero),
                reason: "Changed my mind"))
            .Rows.Single();

        row.StatusDetail.Should().Be("Cancelled 5 Mar 2026");
        row.CancellationReason.Should().Be("Changed my mind");
    }

    [Fact]
    public void Create_ForAPlacedOrder_HasNoStatusDetail()
    {
        Report(Row(Jan12)).Rows.Single().StatusDetail.Should().BeNull();
    }
}
