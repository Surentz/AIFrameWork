using System.Net.Http.Json;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Api.IntegrationTests.Orders;

public sealed class OutboxDeliveryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task PostOrders_WritesAPendingOutboxRow()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-E2E-1", Quantity = 2 });
        var orderId = await response.Content.ReadFromJsonAsync<Guid>();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var row = await context.Outbox.AsNoTracking()
            .SingleAsync(m => m.Payload.Contains(orderId.ToString()));

        row.EventName.Should().Be("order.placed");
        row.Status.Should().Be(OutboxStatus.Pending);
    }

    [Fact]
    public async Task PostOrders_ThenDrainingTheOutbox_DeliversToTheHandler()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-E2E-2", Quantity = 1 });
        var orderId = await response.Content.ReadFromJsonAsync<Guid>();

        await factory.DrainOutboxOnceAsync();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        var audit = await context.OrderAudits.AsNoTracking()
            .SingleOrDefaultAsync(a => a.OrderId == orderId);
        audit.Should().NotBeNull("the handler must have run");

        var row = await context.Outbox.AsNoTracking()
            .SingleAsync(m => m.Payload.Contains(orderId.ToString()));
        row.Status.Should().Be(OutboxStatus.Processed);
    }

    [Fact]
    public async Task RedeliveringAProcessedMessage_LeavesOneAuditRow()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-E2E-3", Quantity = 1 });
        var orderId = await response.Content.ReadFromJsonAsync<Guid>();

        await factory.DrainOutboxOnceAsync();
        await factory.RedeliverAsync(orderId);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        (await context.OrderAudits.AsNoTracking().CountAsync(a => a.OrderId == orderId))
            .Should().Be(1, "the handler is idempotent, so redelivery adds nothing");
    }
}
