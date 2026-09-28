using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using AiFramework.Api.IntegrationTests.Orders;
using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AiFramework.Api.IntegrationTests.Messaging;

/// <summary>
/// The reason for the durable outbox (spec section 3, approach A): a broker outage delays an event,
/// it never loses one, and it never disturbs the request that caused it.
/// </summary>
/// <remarks>
/// Runs inside ApiFactoryCollection, whose tests run one at a time, so stopping the shared broker's
/// application cannot break a neighbour mid-flight. It always restarts the broker app in a finally.
/// This uses <c>rabbitmqctl stop_app</c>/<c>start_app</c> (ApiFactory.StopBrokerAppAsync), not
/// Testcontainers' PauseAsync/UnpauseAsync — see that method's remarks: a paused container is a TCP
/// black hole that blocks the publishing call itself, which would hang DrainOutboxUntilEmptyAsync
/// below and test a network partition rather than a broker outage (plan V4c/V4e).
/// </remarks>
[Collection(nameof(ApiFactoryCollection))]
public sealed class BrokerOutageTests(ApiFactory factory)
{
    [Fact]
    public async Task AnEventPublishedDuringAnOutage_IsDeliveredAfterIt()
    {
        // Unique per run: a fixed name would collide with a previous (or, if this test is ever
        // repeated in the same run, a concurrent) run's queue.
        var queueName = $"outage-test-{Guid.NewGuid():N}";

        // Bound BEFORE stopping the broker app, and durable/non-exclusive rather than
        // BindTemporaryQueueAsync's server-named exclusive queue: an exclusive queue dies with its
        // declaring connection, and stop_app below closes every connection, including this one's.
        // A durable queue survives the restart, so a later probe can reopen it by name.
        await using (var setupProbe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString))
        {
            await setupProbe.BindDurableQueueAsync(queueName, RabbitMqTopology.EventsExchange, "order.placed");
        }

        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(factory);

        await factory.StopBrokerAppAsync();
        try
        {
            var placed = await client.PostAsJsonAsync("/api/orders", new { Sku = sku, Quantity = 1 });

            placed.StatusCode.Should().Be(HttpStatusCode.Created, "a broker outage must not fail a request");
            await factory.DrainOutboxUntilEmptyAsync(); // the fan-out completes: publishing only wrote a row
            (await PendingOutgoingAsync()).Should().BeGreaterThan(0, "the event waits in Postgres");
        }
        finally
        {
            await factory.StartBrokerAppAsync();
        }

        // A fresh connection: stop_app above closed the setup probe's connection too. Wolverine's
        // sending agent retries on its own schedule after its circuit breaker (delivered ~5s after
        // start_app per V4e), which is why this wait is longer than OutboundEventTests' 30s.
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        try
        {
            var delivered = await probe.WaitForMessageAsync(queueName, TimeSpan.FromSeconds(60));
            delivered.Should().NotBeNull("the durable outbox must drain once the broker is back");
        }
        finally
        {
            await probe.DeleteQueueAsync(queueName);
        }
    }

    /// <summary>
    /// Rows waiting for this test's own message, not the whole table: every Api.IntegrationTests
    /// class shares one database via ApiFactoryCollection, so counting unfiltered rows could pick
    /// up another test's leftovers and pass for the wrong reason.
    /// </summary>
    private async Task<long> PendingOutgoingAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();
        // Table and column names per plan V4.
        await using var command = new NpgsqlCommand(
            "select count(*) from wolverine.wolverine_outgoing_envelopes where message_type like '%OrderPlacedV1%'",
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
