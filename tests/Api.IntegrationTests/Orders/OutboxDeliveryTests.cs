using System.Net;
using System.Net.Http.Json;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Api.IntegrationTests.Orders;

[Collection(nameof(ApiFactoryCollection))]
public sealed class OutboxDeliveryTests(ApiFactory factory)
{
    [Fact]
    public async Task PostOrders_WritesAPendingOutboxRow()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-E2E-1", Quantity = 2 });
        response.StatusCode.Should().Be(HttpStatusCode.Created, "a rejected order would never reach the outbox");
        var orderId = await response.Content.ReadFromJsonAsync<Guid>();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var row = await context.Outbox.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Payload.Contains(orderId.ToString()));

        row.Should().NotBeNull("placing an order must write exactly one outbox row for it");
        row.EventName.Should().Be("order.placed");
        row.Status.Should().Be(OutboxStatus.Pending);
    }

    [Fact]
    public async Task PostOrders_ThenDrainingTheOutbox_RunsTheHandler()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-E2E-2", Quantity = 1 });
        response.StatusCode.Should().Be(HttpStatusCode.Created, "a rejected order would never reach the outbox");
        var orderId = await response.Content.ReadFromJsonAsync<Guid>();

        await factory.DrainOutboxOnceAsync();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        var audit = await context.OrderAudits.AsNoTracking()
            .SingleOrDefaultAsync(a => a.OrderId == orderId);
        audit.Should().NotBeNull("the handler must have run");
    }

    [Fact]
    public async Task PostOrders_ThenDrainingTheOutbox_MarksTheOutboxRowProcessed()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-E2E-3", Quantity = 1 });
        response.StatusCode.Should().Be(HttpStatusCode.Created, "a rejected order would never reach the outbox");
        var orderId = await response.Content.ReadFromJsonAsync<Guid>();

        await factory.DrainOutboxOnceAsync();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        var row = await context.Outbox.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Payload.Contains(orderId.ToString()));
        row.Should().NotBeNull("the row written by PostOrders must still exist after draining");
        row.Status.Should().Be(OutboxStatus.Processed);
    }

    [Fact]
    public async Task RedeliveringAProcessedMessage_DoesNotDuplicateTheAuditRow()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-E2E-4", Quantity = 1 });
        response.StatusCode.Should().Be(HttpStatusCode.Created, "a rejected order would never reach the outbox");
        var orderId = await response.Content.ReadFromJsonAsync<Guid>();

        await factory.DrainOutboxOnceAsync();
        await factory.RedeliverAsync(orderId);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        (await context.OrderAudits.AsNoTracking().CountAsync(a => a.OrderId == orderId))
            .Should().Be(1, "the handler is idempotent, so redelivery adds nothing");
    }

    // This is the discriminator: without ON CONFLICT ("MessageId") DO NOTHING in
    // OrderAuditWriter, the duplicate INSERT on redelivery throws a PostgresException that
    // OutboxWorkItemProcessor.ProcessAsync's catch swallows into FailAsync — which also leaves
    // exactly one audit row (rejected by the database rather than skipped by the writer), so
    // RedeliveringAProcessedMessage_DoesNotDuplicateTheAuditRow's count-based assertion alone
    // cannot tell a genuinely idempotent redelivery apart from a redelivery that failed and got
    // queued for more retries. Asserting the outbox row is still Processed, with no LastError,
    // is what tells them apart.
    [Fact]
    public async Task RedeliveringAProcessedMessage_SucceedsInsteadOfBeingRetried()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-E2E-5", Quantity = 1 });
        response.StatusCode.Should().Be(HttpStatusCode.Created, "a rejected order would never reach the outbox");
        var orderId = await response.Content.ReadFromJsonAsync<Guid>();

        await factory.DrainOutboxOnceAsync();
        await factory.RedeliverAsync(orderId);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        var row = await context.Outbox.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Payload.Contains(orderId.ToString()));
        row.Should().NotBeNull("the row written by PostOrders must still exist after redelivery");
        row.Status.Should().Be(OutboxStatus.Processed,
            "a genuinely idempotent redelivery completes; it does not fall back into FailAsync and get flipped to Pending for more retries");
        row.LastError.Should().BeNull("a successful redelivery must not record a failure");
    }
}
