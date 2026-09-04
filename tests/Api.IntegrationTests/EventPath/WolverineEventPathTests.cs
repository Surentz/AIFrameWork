using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace AiFramework.Api.IntegrationTests.EventPath;

/// <summary>
/// The ADR 0005 spike's proof. These tests exist to answer three questions before the
/// hand-built outbox is retired in favour of Wolverine, and they are expected to be deleted
/// along with the spike if the answer turns out to be no.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class WolverineEventPathTests(ApiFactory factory)
{
    [Fact]
    public async Task PublishingANotification_ReachesItsHandler()
    {
        var orderId = Guid.NewGuid();
        var recorder = factory.Services.GetRequiredService<OrderPlacedNotificationRecorder>();
        await using var scope = factory.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        // InvokeAsync, not PublishAsync: it runs the handler inline and completes only when the
        // handler has. PublishAsync would enqueue and return, leaving this test racing a
        // background worker — and tests/CLAUDE.md forbids sleeping or polling to close that gap.
        // What this therefore proves is the wiring (registration, discovery, handler resolution),
        // not the durable queue hop. The queue is covered by the schema test below.
        await bus.InvokeAsync(new OrderPlacedNotification(orderId, "SKU-WOLVERINE-1", 2));

        recorder.WasHandled(orderId).Should().BeTrue(
            "a message invoked through Wolverine must reach the handler registered via IncludeType");
        recorder.TimesHandled(orderId).Should().Be(1, "one invocation is one delivery");
    }

    [Fact]
    public async Task Wolverine_ProvisionsItsEnvelopeTables_InItsOwnSchema()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "schema";
        parameter.Value = WolverineEventPath.EnvelopeSchema;
        command.Parameters.Add(parameter);

        var tableCount = Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture);

        // The point is not the number. It is that Wolverine created these tables itself, on
        // startup, in a schema `dotnet ef migrations` knows nothing about — the "two schema
        // authorities in one database" cost ADR 0005 accepts, made visible and asserted.
        tableCount.Should().BeGreaterThan(0,
            "Wolverine must provision its own envelope storage in its own schema");
    }

    [Fact]
    public async Task PublishingThroughWolverine_WritesNothingToTheHandBuiltOutbox()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var before = await context.Outbox.AsNoTracking().CountAsync(CancellationToken.None);

        await bus.InvokeAsync(new OrderPlacedNotification(Guid.NewGuid(), "SKU-WOLVERINE-2", 1));

        var after = await context.Outbox.AsNoTracking().CountAsync(CancellationToken.None);

        // The spike's premise is that the two paths coexist without touching each other. This is
        // the assertion that would break first if Wolverine's conventional discovery were ever
        // re-enabled and started claiming this repo's own *Handler types.
        after.Should().Be(before,
            "the Wolverine path and the hand-built outbox must stay independent while both run");
    }
}
