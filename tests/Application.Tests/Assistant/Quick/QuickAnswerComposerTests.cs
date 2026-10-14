using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Application.Monitoring;
using AiFramework.Application.Orders;
using AiFramework.Application.Products;
using AiFramework.Application.Statistics;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class QuickAnswerComposerTests
{
    private static readonly DateTimeOffset Placed = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Shipped = new(2026, 10, 3, 14, 12, 0, TimeSpan.Zero);
    private static readonly Guid OrderId = Guid.Parse("3f2a1b9c-0000-4000-8000-000000000001");

    private readonly IQueryDispatcher _queries = Substitute.For<IQueryDispatcher>();

    private QuickAnswerComposer Composer => new(_queries, new ParameterResolver(_queries));

    private static QuickIntent Intent(string id) => QuickIntents.Find(id) ?? throw new InvalidOperationException(id);

    private Task<QuickReply> ComposeAsync(string intent, string question) =>
        Composer.ComposeAsync(Intent(intent), question, answeredAs: null, CancellationToken.None);

    private static OrderView Order(OrderStatus status) => new(
        OrderId, "SKU-1042", 3, Placed, Guid.NewGuid(), "Widget Pro", 19.95m, status,
        status == OrderStatus.Shipped ? Shipped : null, status == OrderStatus.Cancelled ? Shipped : null);

    private void NewestOrderIs(OrderView order)
    {
        _queries.SendAsync(Arg.Is<GetOrders>(q => q.Limit == 1), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderPage([new OrderListItem(order.Id, order.Sku, order.Quantity, order.PlacedAt, order.ProductId, order.ProductName, order.UnitPrice, order.Status)], null)));
        _queries.SendAsync(Arg.Is<GetOrder>(q => q.Id == order.Id), Arg.Any<CancellationToken>())
            .Returns(Result.Success(order));
    }

    [Fact]
    public async Task OrderStatus_ForAShippedLastOrder_SaysWhenItShipped_AndLinksToIt()
    {
        NewestOrderIs(Order(OrderStatus.Shipped));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, "Has my last order shipped?");

        answer.Text.Should().Be("Your last order shipped on 3 Oct 2026, 14:12 UTC.");
        answer.Facts.Should().Contain(new QuickFact("Product", "Widget Pro (SKU-1042)"))
            .And.Contain(new QuickFact("Quantity", "3 × 19.95"))
            .And.Contain(new QuickFact("Status", "Shipped"));
        answer.Link.Should().Be(new QuickLink("Open order", $"/orders/{OrderId}"));
        answer.Kind.Should().Be(QuickAnswerKind.Instant);
    }

    [Fact]
    public async Task OrderStatus_ForAPlacedOrder_SaysItHasNotShipped()
    {
        NewestOrderIs(Order(OrderStatus.Placed));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, "Has my last order shipped?");

        answer.Text.Should().Be("Your last order has not shipped yet. It was placed on 1 Oct 2026, 09:30 UTC.");
    }

    [Fact]
    public async Task OrderStatus_ByFullId_ReadsThatOrder()
    {
        _queries.SendAsync(Arg.Is<GetOrder>(q => q.Id == OrderId), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Order(OrderStatus.Cancelled)));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, $"what happened to order {OrderId}");

        answer.Text.Should().Be("Order 3f2a1b9c was cancelled on 3 Oct 2026, 14:12 UTC.");
    }

    [Fact]
    public async Task OrderStatus_ForAnOrderThatIsNotTheCallers_SaysItWasNotFound()
    {
        _queries.SendAsync(Arg.Any<GetOrder>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<OrderView>(new Error(ErrorKind.NotFound, "orders.not_found", "No such order.")));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, $"status of order {OrderId}");

        answer.Text.Should().Be("I couldn't find order 3f2a1b9c among your orders.");
        answer.Link.Should().Be(new QuickLink("All orders", "/orders"));
    }

    [Fact]
    public async Task OrderStatus_WithNoOrders_SaysSo_AndLinksToPlaceOne()
    {
        _queries.SendAsync(Arg.Any<GetOrders>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new OrderPage([], null)));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, "Has my last order shipped?");

        answer.Text.Should().Be("You haven't placed any orders yet.");
        answer.Link!.To.Should().Be("/orders/new");
    }

    [Fact]
    public async Task OrderStatus_WhenTheOrdersCannotBeRead_SaysSo_WithoutGuessing()
    {
        _queries.SendAsync(Arg.Any<GetOrders>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<OrderPage>(new Error(ErrorKind.Unavailable, "db.down", "Down.")));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, "Has my last order shipped?");

        answer.Text.Should().Be("Your orders couldn't be loaded right now. Try again in a moment.");
        answer.Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task RecentOrders_ListsThemInATable()
    {
        _queries.SendAsync(Arg.Is<GetOrders>(q => q.Limit == QuickAnswerComposer.RecentOrderCount), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderPage([new OrderListItem(OrderId, "SKU-1042", 3, Placed, null, "Widget Pro", 19.95m, OrderStatus.Placed)], null)));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.RecentOrders, "Show my recent orders");

        answer.Text.Should().Be("Your most recent order:");
        answer.Table!.Columns.Should().Equal("Order", "Product", "Quantity", "Status", "Placed");
        answer.Table.Rows.Should().ContainSingle().Which.Should().Equal("3f2a1b9c", "Widget Pro", "3", "Placed", "1 Oct 2026, 09:30 UTC");
        answer.Link!.To.Should().Be("/orders");
    }

    [Fact]
    public async Task ExportStatus_ForAReadyExport_GivesTheRowCount()
    {
        _queries.SendAsync(Arg.Any<GetOrderExports>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<OrderExportView>>([new OrderExportView(Guid.NewGuid(), OrderExportState.Ready, Placed, Shipped, 42)]));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.ExportStatus, "Is my export ready?");

        answer.Text.Should().Be("Your latest export is ready: 42 orders, finished on 3 Oct 2026, 14:12 UTC.");
        answer.Link!.To.Should().Be("/orders/exports");
    }

    [Fact]
    public async Task ProductInfo_ForOneClearMatch_GivesItsPrice()
    {
        var id = Guid.NewGuid();
        _queries.SendAsync(Arg.Any<FindProducts>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ProductMatch>>([new ProductMatch(id, "SKU-1042", "Widget Pro", 19.95m, 1)]));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.ProductInfo, "how much is the widget pro");

        answer.Text.Should().Be("Widget Pro costs 19.95.");
        answer.Link.Should().Be(new QuickLink("Open product", $"/products/{id}"));
    }

    [Fact]
    public async Task ProductInfo_ForACloseMatch_SaysItIsTheClosest()
    {
        _queries.SendAsync(Arg.Any<FindProducts>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ProductMatch>>([new ProductMatch(Guid.NewGuid(), "SKU-1042", "Widget Pro", 19.95m, 0.7)]));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.ProductInfo, "how much is the widjet pro");

        answer.Text.Should().Be("Widget Pro, the closest match, costs 19.95.");
    }

    [Fact]
    public async Task ProductInfo_ForTwoSimilarProducts_AsksWhichOne_NeverPicks()
    {
        _queries.SendAsync(Arg.Any<FindProducts>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ProductMatch>>(
            [
                new ProductMatch(Guid.NewGuid(), "W-1", "Widget", 9.95m, 0.8),
                new ProductMatch(Guid.NewGuid(), "W-2", "Widget Pro", 19.95m, 0.75),
            ]));

        var reply = await ComposeAsync(QuickIntents.ProductInfo, "price of the widgt");

        var suggestions = reply.Should().BeOfType<QuickSuggestions>().Subject;
        suggestions.Text.Should().Be("Which product did you mean?");
        suggestions.Options.Should().Equal("How much is Widget?", "How much is Widget Pro?");
    }

    [Fact]
    public async Task ProductInfo_WhenOneExactNameContainsAnother_NamesTheLongerOne()
    {
        // word_similarity scores every name contained in the question 1.0, and the shorter sorts first.
        var pro = Guid.NewGuid();
        _queries.SendAsync(Arg.Any<FindProducts>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ProductMatch>>(
            [
                new ProductMatch(Guid.NewGuid(), "W-1", "Widget", 9.95m, 1),
                new ProductMatch(pro, "W-2", "Widget Pro", 19.95m, 1),
            ]));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.ProductInfo, "How much is the Widget Pro?");

        answer.Text.Should().Be("Widget Pro costs 19.95.");
        answer.Facts.Should().Contain(new QuickFact("SKU", "W-2"));
        answer.Link.Should().Be(new QuickLink("Open product", $"/products/{pro}"));
    }

    [Fact]
    public async Task ProductInfo_ForTwoUnrelatedExactNames_AsksWhichOne_NeverPicks()
    {
        _queries.SendAsync(Arg.Any<FindProducts>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ProductMatch>>(
            [
                new ProductMatch(Guid.NewGuid(), "G-1", "Gadget", 4.95m, 1),
                new ProductMatch(Guid.NewGuid(), "W-1", "Widget", 9.95m, 1),
                new ProductMatch(Guid.NewGuid(), "W-3", "Widget Max", 29.95m, 0.6),
            ]));

        var reply = await ComposeAsync(QuickIntents.ProductInfo, "is the widget dearer than the gadget?");

        var suggestions = reply.Should().BeOfType<QuickSuggestions>().Subject;
        suggestions.Text.Should().Be("Which product did you mean?");
        suggestions.Options.Should().Equal("How much is Gadget?", "How much is Widget?");
    }

    [Fact]
    public async Task Population_ForAResolvedArea_GivesTheFigure()
    {
        _queries.SendAsync(Arg.Any<GetPopulationAreas>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PopulationAreasView([new PopulationAreaView("101", "Copenhagen")], "Statistics Denmark")));
        _queries.SendAsync(Arg.Is<GetPopulation>(q => q.Area == "101"), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PopulationView("101", "Copenhagen", "2026Q3", 667099, "Statistics Denmark")));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.Population, "population of Copenhagen");

        answer.Text.Should().Be("Copenhagen had 667,099 inhabitants in 2026Q3.");
        answer.Link!.To.Should().Be("/statistics/population");
    }

    [Fact]
    public async Task JobHealth_WithNoFailures_SaysSo()
    {
        _queries.SendAsync(Arg.Is<GetJobHealth>(q => q.Window == TimeSpan.FromHours(24)), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new JobHealthView(1, 40, 0, 0, Placed)));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.JobHealth, "Did any background job fail today?");

        answer.Text.Should().Be("No background job failed in the last 24 hours.");
        answer.Link!.To.Should().Be("/monitoring/jobs");
    }

    [Fact]
    public async Task FailedJobs_ShowsNoErrorText()
    {
        var run = new JobRunView(Guid.NewGuid(), 2, "SendOrderConfirmation", null, JobRunStatus.Failed, Placed, Shipped, 120, null, "secret detail", null, null);
        _queries.SendAsync(Arg.Any<GetJobRuns>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new JobRunPage([run], 7, 1)));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.FailedJobs, "Which jobs failed?");

        answer.Table!.Rows.SelectMany(r => r).Should().NotContain(cell => cell.Contains("secret", StringComparison.Ordinal));
        answer.Text.Should().Be("The most recent failed job run, of 7:");
    }

    [Fact]
    public async Task SignIns_GivesCountsOnly_NeverALockedOutUsername()
    {
        _queries.SendAsync(Arg.Any<GetSignInHealth>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SignInHealthView(
                40, 3, 2, 1, Placed, new ActiveUsersView(5, TimeSpan.FromMinutes(15)),
                [new LockedOutUserView(Guid.NewGuid(), "jens.hansen", Shipped)])));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.SignIns, "Any lockouts?");

        answer.Facts.Should().Contain(new QuickFact("Locked out now", "1"));
        Everything(answer).Should().NotContain("jens.hansen");
    }

    [Fact]
    public async Task DeadLetters_NeverShowTheExceptionMessage()
    {
        var letter = new DeadLetterView(
            Guid.NewGuid(), "SendOrderConfirmation", "SmtpException", "Mailbox jens.hansen@example.com is full", Placed, null, Replayable: true);
        _queries.SendAsync(Arg.Any<GetDeadLetters>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DeadLetterPage([letter], 1, 1)));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.DeadLetters, "Anything dead-lettered?");

        answer.Table!.Rows.Should().ContainSingle().Which.Should().Contain("SmtpException");
        Everything(answer).Should().NotContain("jens.hansen");
    }

    [Fact]
    public async Task Integrations_NeverShowAHealthCheckDescription()
    {
        var system = new ExternalSystemRowView(
            "StatBank", ExternalSystemState.Unhealthy, "Token request as client jens.hansen was refused", Placed, Stale: false, null, false, 10, 2, 0, 12, 80);
        _queries.SendAsync(Arg.Any<GetExternalSystemStatus>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ExternalSystemsView(Placed, [system])));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.Integrations, "Are the partner systems up?");

        answer.Text.Should().Be("1 of 1 external systems need attention.");
        Everything(answer).Should().NotContain("jens.hansen");
    }

    // Every string the answer carries, and the replay text Claude would later be sent.
    private static string Everything(QuickAnswer answer) => string.Join(
        '\n',
        new[] { answer.Title, answer.Text, answer.AnsweredAs, answer.Link?.Label, answer.Link?.To, QuickReplay.For(answer) }
            .Concat(answer.Facts.SelectMany(f => new[] { f.Label, f.Value }))
            .Concat(answer.Table?.Columns ?? [])
            .Concat(answer.Table?.Rows.SelectMany(r => r) ?? [])
            .OfType<string>());

    [Fact]
    public async Task Traffic_FormatsTheErrorRateAsAPercentage()
    {
        var overall = new TrafficRowView(default, "all", 600, 6, 0, null, 20, 80, 120);
        _queries.SendAsync(Arg.Any<GetTrafficSummary>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TrafficSummaryView(Placed, 10, 0.01, overall, [])));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.Traffic, "How is the API doing?");

        answer.Text.Should().Be("In the last hour the API served 10 requests a minute, with an error rate of 1.0%.");
        answer.Facts.Should().Contain(new QuickFact("p95 latency", "80 ms"));
    }

    [Fact]
    public void Help_QuotesTheSection_AndLinksToItsScreen()
    {
        var section = new HelpSection("export-orders", "Exporting your orders", "How do I export my orders as a PDF?", "Request an export", "Open Exports and choose Export my orders.", new QuickLink("Go to Exports", "/orders/exports"));

        var answer = QuickAnswerComposer.Help(section, answeredAs: null);

        answer.Kind.Should().Be(QuickAnswerKind.Help);
        answer.Title.Should().Be("Exporting your orders");
        answer.Text.Should().Be("Open Exports and choose Export my orders.");
        answer.Link.Should().Be(section.Link);
        answer.Facts.Should().BeEmpty();
        answer.Table.Should().BeNull();
    }

    [Fact]
    public async Task EveryIntent_HasAComposer()
    {
        foreach (var intent in QuickIntents.All)
        {
            var act = () => Composer.ComposeAsync(intent, "x", null, CancellationToken.None);

            await act.Should().NotThrowAsync<InvalidOperationException>($"{intent.Id} needs a case in ComposeAsync");
        }
    }
}
