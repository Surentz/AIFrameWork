using System.Text.Json;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using AiFramework.Application.Monitoring;
using AiFramework.Application.Orders;
using AiFramework.Application.Products;
using AiFramework.Application.Statistics;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant;

public sealed class AssistantToolboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private readonly IQueryDispatcher _queries = Substitute.For<IQueryDispatcher>();

    private AssistantToolbox Toolbox => new(_queries);

    private static AssistantToolCall Call(string name, string input) =>
        new("toolu_1", name, JsonDocument.Parse(input).RootElement.Clone());

    [Fact]
    public async Task RunAsync_JobHealth_DispatchesTheWindowAndReturnsTheCounts()
    {
        _queries.SendAsync(Arg.Is<GetJobHealth>(q => q.Window == TimeSpan.FromHours(24)), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new JobHealthView(1, 40, 3, 2, Now.AddHours(-24))));

        var result = await Toolbox.RunAsync(Call("job_health", """{"window_hours":24}"""), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.ToolCallId.Should().Be("toolu_1");
        result.Content.Should().Contain("\"failed\":3").And.Contain("\"deadLettered\":2");
    }

    [Theory]
    [InlineData("""{"window_hours":0}""")]
    [InlineData("""{"window_hours":169}""")]
    [InlineData("""{"window_hours":"24"}""")]
    [InlineData("""{}""")]
    public async Task RunAsync_WindowOutOfRange_ReturnsAnErrorWithoutDispatching(string input)
    {
        var result = await Toolbox.RunAsync(Call("job_health", input), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("window_hours");
        await _queries.DidNotReceiveWithAnyArgs().SendAsync<JobHealthView>(default!, default);
    }

    [Fact]
    public async Task RunAsync_ListJobRuns_ScrubsTheErrorAndOmitsTheOwner()
    {
        var owner = Guid.NewGuid();
        var run = new JobRunView(Guid.NewGuid(), 2, "SendOrderConfirmation", JobLane.Light, JobRunStatus.Failed,
            Now, Now.AddSeconds(3), 3000, owner, "SMTP rejected jane@example.com", "trace-1", "worker-0");
        _queries.SendAsync(
                Arg.Is<GetJobRuns>(q => q.Status == JobRunStatus.Failed && q.Page == 1 && q.PageSize == AssistantToolbox.PageSize),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(new JobRunPage([run], 1, 1)));

        var result = await Toolbox.RunAsync(
            Call("list_job_runs", """{"status":"Failed","job_name":null,"page":1}"""), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("[email]").And.NotContain("jane@example.com");
        result.Content.Should().NotContain(owner.ToString());
    }

    [Fact]
    public async Task RunAsync_ListJobRuns_UnknownStatus_ReturnsAnError()
    {
        var result = await Toolbox.RunAsync(
            Call("list_job_runs", """{"status":"Exploded","job_name":null,"page":1}"""), CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ListDeadLetters_ScrubsTheExceptionMessage()
    {
        var letter = new DeadLetterView(Guid.NewGuid(), "SendOrderConfirmation", "SmtpException",
            "Relay 10.1.2.3 refused", Now, null, Replayable: true);
        _queries.SendAsync(Arg.Any<GetDeadLetters>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DeadLetterPage([letter], 1, 1)));

        var result = await Toolbox.RunAsync(Call("list_dead_letters", """{"page":1}"""), CancellationToken.None);

        result.Content.Should().Contain("Relay [ip] refused").And.NotContain("10.1.2.3");
    }

    [Fact]
    public async Task RunAsync_SignInHealth_ReplacesLockedOutUsersWithACount()
    {
        var view = new SignInHealthView(10, 4, 1, 2, Now.AddHours(-24), new ActiveUsersView(3, TimeSpan.FromMinutes(15)),
            [new LockedOutUserView(Guid.NewGuid(), "mallory", Now.AddMinutes(10))]);
        _queries.SendAsync(Arg.Any<GetSignInHealth>(), Arg.Any<CancellationToken>()).Returns(Result.Success(view));

        var result = await Toolbox.RunAsync(Call("sign_in_health", """{"window_hours":24}"""), CancellationToken.None);

        result.Content.Should().Contain("\"lockedOutAccounts\":1").And.NotContain("mallory");
    }

    [Fact]
    public async Task RunAsync_QueryFails_ReturnsTheErrorMessageAsAnErrorResult()
    {
        _queries.SendAsync(Arg.Any<GetExternalSystemStatus>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ExternalSystemsView>(new Error(ErrorKind.Unavailable, "db.down", "The database is unavailable.")));

        var result = await Toolbox.RunAsync(Call("external_systems", "{}"), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Be("The database is unavailable.");
    }

    [Fact]
    public async Task RunAsync_UnknownTool_ReturnsAnError()
    {
        var result = await Toolbox.RunAsync(Call("drop_tables", "{}"), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Unknown tool");
    }

    [Fact]
    public async Task RunAsync_EveryDefinedTool_IsKnown()
    {
        // external_systems takes no input, so "{}" is valid for it and it dispatches.
        _queries.SendAsync(Arg.Any<GetExternalSystemStatus>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ExternalSystemsView(Now, [])));
        _queries.SendAsync(Arg.Any<GetOrderExports>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<OrderExportView>>([]));

        foreach (var definition in AssistantToolbox.Definitions)
        {
            var result = await Toolbox.RunAsync(Call(definition.Name, "{}"), CancellationToken.None);

            result.Content.Should().NotContain("Unknown tool", because: definition.Name);
        }
    }

    [Fact]
    public void Definitions_EverySchemaForbidsExtraPropertiesAndRequiresAll()
    {
        foreach (var definition in AssistantToolbox.Definitions)
        {
            var schema = JsonDocument.Parse(definition.InputSchema).RootElement;
            schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse(because: definition.Name);
            var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal);
            var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal);
            required.Should().Equal(properties, because: definition.Name);
        }
    }

    [Fact]
    public async Task RunAsync_MyOrders_DispatchesTheFirstPageAndReturnsTheOrders()
    {
        var id = Guid.NewGuid();
        _queries.SendAsync(Arg.Is<GetOrders>(q => q.Limit == AssistantToolbox.PageSize && q.Cursor == null), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderPage([new OrderListItem(id, "SKU-1", 2, Now, null, "Mug", 9.5m, OrderStatus.Placed)], "next")));

        var result = await Toolbox.RunAsync(Call("my_orders", """{"cursor":null}"""), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("\"sku\":\"SKU-1\"").And.Contain("\"status\":\"Placed\"").And.Contain("\"nextCursor\":\"next\"");
    }

    [Fact]
    public async Task RunAsync_MyOrdersWithACursor_PassesItOn()
    {
        _queries.SendAsync(Arg.Is<GetOrders>(q => q.Cursor == "C42"), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderPage([], null)));

        var result = await Toolbox.RunAsync(Call("my_orders", """{"cursor":"C42"}"""), CancellationToken.None);

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_GetOrder_ReturnsTheOrderWithItsDates()
    {
        var id = Guid.NewGuid();
        _queries.SendAsync(Arg.Is<GetOrder>(q => q.Id == id), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderView(id, "SKU-1", 1, Now, null, "Mug", 9.5m, OrderStatus.Shipped, Now.AddDays(1), null)));

        var result = await Toolbox.RunAsync(Call("get_order", $$"""{"order_id":"{{id}}"}"""), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("\"status\":\"Shipped\"").And.Contain("\"shippedAt\"");
    }

    [Fact]
    public async Task RunAsync_GetOrderNotFound_IsAnErrorCarryingTheQueryMessage()
    {
        _queries.SendAsync(Arg.Any<GetOrder>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<OrderView>(new Error(ErrorKind.NotFound, "order.not_found", "No order with id 'x'.")));

        var result = await Toolbox.RunAsync(Call("get_order", $$"""{"order_id":"{{Guid.NewGuid()}}"}"""), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Be("No order with id 'x'.");
    }

    [Theory]
    [InlineData("""{"order_id":"not-a-guid"}""")]
    [InlineData("""{"order_id":null}""")]
    [InlineData("""{}""")]
    public async Task RunAsync_GetOrderWithoutAGuid_IsAnErrorWithoutDispatching(string input)
    {
        var result = await Toolbox.RunAsync(Call("get_order", input), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("order_id");
        await _queries.DidNotReceiveWithAnyArgs().SendAsync<OrderView>(default!, default);
    }

    [Fact]
    public async Task RunAsync_MyExports_ReturnsTheExports()
    {
        _queries.SendAsync(Arg.Any<GetOrderExports>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<OrderExportView>>([new OrderExportView(Guid.NewGuid(), OrderExportState.Ready, Now, Now.AddMinutes(1), 12)]));

        var result = await Toolbox.RunAsync(Call("my_exports", "{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("\"status\":\"Ready\"").And.Contain("\"rowCount\":12");
    }

    [Fact]
    public async Task RunAsync_ListProducts_ReturnsTheCataloguePage()
    {
        _queries.SendAsync(Arg.Is<GetProducts>(q => q.Limit == AssistantToolbox.PageSize && q.Cursor == null), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ProductPage([new ProductListItem(Guid.NewGuid(), "SKU-9", "Teapot", 19.95m, Now)], null)));

        var result = await Toolbox.RunAsync(Call("list_products", """{"cursor":null}"""), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("\"name\":\"Teapot\"").And.Contain("\"price\":19.95");
    }

    [Fact]
    public async Task RunAsync_Population_DispatchesTheAreaCode()
    {
        _queries.SendAsync(Arg.Is<GetPopulation>(q => q.Area == "101"), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PopulationView("101", "København", "2026K3", 667_099, "Statistics Denmark (CC BY 4.0)")));

        var result = await Toolbox.RunAsync(Call("population", """{"area_code":"101"}"""), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("\"population\":667099").And.Contain("Statistics Denmark");
    }

    [Fact]
    public async Task RunAsync_PopulationWithoutAnAreaCode_IsAnErrorWithoutDispatching()
    {
        var result = await Toolbox.RunAsync(Call("population", """{"area_code":null}"""), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("area_code");
        await _queries.DidNotReceiveWithAnyArgs().SendAsync<PopulationView>(default!, default);
    }

    [Fact]
    public void Definitions_AreTheMemberToolsThenTheMonitoringTools()
    {
        AssistantToolbox.MemberDefinitions.Select(d => d.Name).Should()
            .Equal("my_orders", "get_order", "my_exports", "list_products", "population");
        AssistantToolbox.Definitions.Should().Equal([.. AssistantToolbox.MemberDefinitions, .. AssistantToolbox.MonitoringDefinitions]);
        AssistantToolbox.MonitoringDefinitions.Should().HaveCount(8);
    }
}
