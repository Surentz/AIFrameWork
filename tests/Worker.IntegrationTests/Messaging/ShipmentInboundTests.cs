using System.Globalization;
using System.Text;
using System.Text.Json;
using AiFramework.Application.IntegrationEvents;
using AiFramework.Application.Monitoring;
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Integration;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using RabbitMQ.Client.Exceptions;

namespace AiFramework.Worker.IntegrationTests.Messaging;

/// <summary>
/// shipment.confirmed.v1 from an external producer - RabbitMQ.Client, no Wolverine headers -
/// through the worker to the database, and back out as order.shipped.v1. ADR 0026.
/// </summary>
[Collection(nameof(WorkerFactoryCollection))]
public sealed class ShipmentInboundTests(WorkerFactory factory)
{
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wolverine's durability agent POLLS for dead letters marked replayable - it re-ran one ~19 s
    /// after Retry in the plan's Verified API V3 - so a replay needs far longer than a delivery.
    /// </summary>
    private static readonly TimeSpan Replay = TimeSpan.FromSeconds(60);

    // Matched on exception_message rather than the body: IntegrationMessageRejectedException names
    // the order, and the message is text, where the body is bytea.
    private const string DeadLettersForOrder =
        "select count(*) from wolverine.wolverine_dead_letters where exception_message like @text";

    // A dead letter nobody has asked to replay. Retry flips its row to replayable = true, and only
    // a fresh rejection writes one back as false.
    private const string UnreplayedDeadLettersForOrder =
        "select count(*) from wolverine.wolverine_dead_letters " +
        "where exception_message like @text and not replayable";

    /// <summary>An order placed straight through the repository: the worker has no HTTP API.</summary>
    private async Task<Guid> PlaceOrderAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var order = Order.Place(
            Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(-10), AnOrderedProduct.Any(), "SKU-INBOUND");
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order.Id;
    }

    private async Task PublishShipmentAsync(Guid orderId, string shipmentId)
    {
        // The first access to Services starts the worker, and its listener is what declares the
        // queue. Published before that, to a queue that does not exist yet, the message is dropped
        // - which is how a test run on its own failed while the same test passed in a full run.
        _ = factory.Services;

        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var json = JsonSerializer.Serialize(
            new ShipmentConfirmedV1(shipmentId, orderId, DateTimeOffset.UtcNow),
            IntegrationJson.Options);
        await probe.PublishToQueueAsync(RabbitMqTopology.ShipmentsQueue, json);
    }

    private async Task<OrderStatus?> StatusAsync(Guid orderId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        return await context.Orders.AsNoTracking().Where(o => o.Id == orderId)
            .Select(o => (OrderStatus?)o.Status).SingleOrDefaultAsync();
    }

    /// <summary>
    /// Bounded wait on the database, for the same reason BrokerProbe.WaitForMessageAsync gives:
    /// the message crosses the broker, another process, before the worker handles it.
    /// </summary>
    private async Task<bool> WaitForStatusAsync(Guid orderId, OrderStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await StatusAsync(orderId) == expected)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private async Task<long> CountAsync(string sql, NpgsqlParameter parameter)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private Task<long> DeadLettersForAsync(Guid orderId, string sql = DeadLettersForOrder) =>
        CountAsync(sql, new NpgsqlParameter("text", $"%{orderId}%"));

    /// <summary>Bounded, for the reason <see cref="WaitForStatusAsync"/> gives.</summary>
    private async Task<bool> WaitForDeadLetterAsync(Guid orderId, TimeSpan timeout, string sql = DeadLettersForOrder)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await DeadLettersForAsync(orderId, sql) > 0)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    [Fact]
    public async Task AShipment_ShipsTheOrder()
    {
        var orderId = await PlaceOrderAsync();

        await PublishShipmentAsync(orderId, "WH-SHIPS");

        (await WaitForStatusAsync(orderId, OrderStatus.Shipped, Delivery)).Should().BeTrue();
    }

    [Fact]
    public async Task AShipment_PublishesOrderShippedV1_TheRoundTrip()
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var queue = await probe.BindTemporaryQueueAsync(RabbitMqTopology.EventsExchange, "order.shipped");
        var orderId = await PlaceOrderAsync();

        await PublishShipmentAsync(orderId, "WH-ROUNDTRIP");

        // The worker's own outbox pump runs the fan-out (WorkerFactory keeps its hosted services).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var found = false;
        while (!found && DateTime.UtcNow < deadline)
        {
            var got = await probe.WaitForMessageAsync(queue, deadline - DateTime.UtcNow);
            if (got is null)
            {
                break;
            }

            found = string.Equals(got.BasicProperties.Type, OrderShippedV1.TypeName, StringComparison.Ordinal)
                && Encoding.UTF8.GetString(got.Body.Span).Contains(orderId.ToString(), StringComparison.Ordinal);
        }

        found.Should().BeTrue("shipping through the broker must publish order.shipped.v1 back out");
    }

    [Fact]
    public async Task ADuplicateShipment_IsANoOpAndDeadLettersNothing()
    {
        var orderId = await PlaceOrderAsync();
        await PublishShipmentAsync(orderId, "WH-DUP");
        (await WaitForStatusAsync(orderId, OrderStatus.Shipped, Delivery)).Should().BeTrue();

        await PublishShipmentAsync(orderId, "WH-DUP");
        await PublishShipmentAsync(orderId, "WH-DUP-2");

        // A marker message after the duplicates: once it is handled, the duplicates were too.
        var marker = await PlaceOrderAsync();
        await PublishShipmentAsync(marker, "WH-MARKER");
        (await WaitForStatusAsync(marker, OrderStatus.Shipped, Delivery)).Should().BeTrue();

        (await DeadLettersForAsync(orderId)).Should().Be(0);
    }

    [Fact]
    public async Task AShipmentForAnUnknownOrder_DeadLettersOnce()
    {
        var unknown = Guid.NewGuid();

        await PublishShipmentAsync(unknown, "WH-UNKNOWN");

        (await WaitForDeadLetterAsync(unknown, Delivery)).Should().BeTrue(
            "a rejection must land on the monitoring page's dead letters, not retry forever");
    }

    [Fact]
    public async Task AShipmentForACancelledOrder_DeadLetters()
    {
        var orderId = await PlaceOrderAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
            var order = await context.Orders.SingleAsync(o => o.Id == orderId);
            order.Cancel("Out of stock.", DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }

        await PublishShipmentAsync(orderId, "WH-CANCELLED");

        (await WaitForDeadLetterAsync(orderId, Delivery)).Should().BeTrue();
        (await StatusAsync(orderId)).Should().Be(OrderStatus.Cancelled);
    }

    // V3: the monitoring page's Retry must still work when the transport is RabbitMQ.
    [Fact]
    public async Task ADeadLetteredShipment_IsRedeliveredByRetry()
    {
        var unknown = Guid.NewGuid();
        await PublishShipmentAsync(unknown, "WH-RETRY");
        (await WaitForDeadLetterAsync(unknown, Delivery)).Should().BeTrue();

        await using var scope = factory.Services.CreateAsyncScope();
        var deadLetters = scope.ServiceProvider.GetRequiredService<IDeadLetterStore>();
        var page = await deadLetters.ListAsync(1, 100, CancellationToken.None);
        var entry = page.Items.Single(i => i.MessageType.Contains("ShipmentConfirmed", StringComparison.Ordinal)
            && i.ExceptionMessage.Contains(unknown.ToString(), StringComparison.Ordinal));

        (await deadLetters.ReplayAsync(entry.Id, CancellationToken.None)).Should().BeTrue();
        // The replay took effect: the original row is no longer an unreplayed dead letter. Without
        // this, the wait below could pass on that original row alone.
        (await DeadLettersForAsync(unknown, UnreplayedDeadLettersForOrder)).Should().Be(0,
            "Retry marks the row replayable (or the durability agent has already taken it)");

        // The order is still unknown, so the re-run handler rejects again and the dead letter is
        // written back unreplayed - which only a second run of the handler can do. Observed: the
        // same envelope id, one row, ~19 s after Retry.
        (await WaitForDeadLetterAsync(unknown, Replay, UnreplayedDeadLettersForOrder)).Should().BeTrue(
            "Retry must run the handler again");
    }

    // A producer's bug must not become an outage: V5/V5b found the JsonException dead-letters at
    // once, even under the worker's global ScheduleRetry policy, so no policy of its own is needed.
    [Fact]
    public async Task AShipmentThatIsNotJson_DeadLettersAndTheQueueKeepsMoving()
    {
        _ = factory.Services; // see PublishShipmentAsync
        var garbage = $"this is not json {{ {Guid.NewGuid()}";
        await using (var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString))
        {
            await probe.PublishToQueueAsync(RabbitMqTopology.ShipmentsQueue, garbage);
        }

        var next = await PlaceOrderAsync();
        await PublishShipmentAsync(next, "WH-AFTER-GARBAGE");

        (await WaitForStatusAsync(next, OrderStatus.Shipped, Delivery)).Should().BeTrue(
            "a poison message must not block the queue behind it");

        // It never deserialized, so no exception names an order: match the stored body instead.
        (await CountAsync(
            "select count(*) from wolverine.wolverine_dead_letters where position(@body in body) > 0",
            new NpgsqlParameter("body", NpgsqlDbType.Bytea) { Value = Encoding.UTF8.GetBytes(garbage) }))
            .Should().Be(1, "the poison message is parked where the monitoring page can show it");
    }

    // Review Focus 5. stop_app/start_app rather than pausing the container: a real broker restart
    // closes every AMQP connection, where a pause is a TCP black hole (plan, Verified API V4e).
    [Fact]
    public async Task AfterABrokerOutage_TheWorkerStillConsumesShipments()
    {
        var orderId = await PlaceOrderAsync();

        await factory.StopBrokerAppAsync();
        try
        {
            // The outage is real: nothing can connect, the worker's listener included.
            await FluentActions.Awaiting(() => BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString))
                .Should().ThrowAsync<BrokerUnreachableException>();
        }
        finally
        {
            // Always, so a failure here cannot leave the rest of the collection without a broker.
            await factory.StartBrokerAppAsync();
        }

        await PublishShipmentAsync(orderId, "WH-RECONNECT");

        // Longer than Delivery: the listener reconnects on its own backoff (V4e: ~6 s).
        (await WaitForStatusAsync(orderId, OrderStatus.Shipped, TimeSpan.FromSeconds(90)))
            .Should().BeTrue("the listener must reconnect on its own after the broker returns");
    }
}
