using AiFramework.Api.IntegrationTests.Diagnostics;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace AiFramework.Api.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // The parameterless PostgreSqlBuilder() + WithImage(...) pairing is obsolete in
    // Testcontainers.PostgreSql 4.14.0 (CS0618); the image now goes to the constructor.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    // The container must start BEFORE anything touches Services: the first access to
    // Services builds the host, which runs ConfigureWebHost, which reads the container's
    // connection string. Reversing these two lines fails with a connection error.
    async Task IAsyncLifetime.InitializeAsync()
    {
        await _container.StartAsync();
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>()
            .Database.MigrateAsync();
    }

    // Explicit interface implementation: WebApplicationFactory already exposes a
    // ValueTask DisposeAsync() from IAsyncDisposable, so declaring xUnit's
    // Task DisposeAsync() implicitly would hide it and leak the host.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await _container.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Program.cs now throws at startup when ConnectionStrings:Default is null or
        // whitespace (see Program.cs). appsettings.json ships "" for that key, so this
        // setting must be supplied before the host is built, not swapped in afterward via
        // ConfigureServices below — otherwise the stricter guard throws first.
        builder.UseSetting("ConnectionStrings:Default", _container.GetConnectionString());

        builder.ConfigureServices(services =>
        {
            // No DbContextOptions<AiFrameworkDbContext> override here: UseSetting above already
            // points Program.cs's own AddInfrastructure(connectionString) call at the container
            // before the host is built, so production's registration — including
            // AddInterceptors(DomainEventsInterceptor) — already targets the test database.
            // Removing and re-registering it here (as an earlier version of this file did) is not
            // just redundant: EF Core composes DbContext configuration across every AddDbContext
            // call for a type (via IDbContextOptionsConfiguration<TContext>) rather than having
            // the later call fully replace the earlier one, so a second bare AddDbContext call
            // does not even drop the interceptor added by the first — it just re-set the same
            // connection string. Confirmed empirically: OutboxDeliveryTests.
            // PostOrders_WritesAPendingOutboxRow passes against this factory either way.

            // The outbox pumps are removed here deliberately. They would compete with
            // DrainOutboxOnceAsync for the same rows and make outbox tests timing-dependent.
            // The drain helper below invokes the same OutboxPoller and OutboxWorkItemProcessor
            // the pumps use, so the wiring under test is still the real one. Confirmed (by
            // grepping AddHostedService usage) that OutboxPollerService and OutboxWorkerService
            // are the only two hosted services this app registers, so a blanket removal and a
            // narrowed one are equivalent today.
            services.RemoveAll<IHostedService>();

            // Test-only endpoints that throw on demand, exercising GlobalExceptionHandler's
            // two branches over real HTTP. See TestEndpointsStartupFilter for why this is an
            // IStartupFilter rather than a controller.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IStartupFilter, TestEndpointsStartupFilter>());
        });
    }

    /// <summary>
    /// Runs one claim-and-process cycle synchronously. Drives the same OutboxPoller and
    /// OutboxWorkItemProcessor the hosted services use, so the wiring under test is real —
    /// but deterministically, without waiting on BackgroundService timing. Each claimed item
    /// gets its own scope, mirroring OutboxWorkerService.ProcessOneAsync: production never
    /// shares one DbContext across items, so neither does this.
    /// </summary>
    public async Task DrainOutboxOnceAsync()
    {
        IReadOnlyList<OutboxWorkItem> claimed;
        await using (var pollScope = Services.CreateAsyncScope())
        {
            var poller = pollScope.ServiceProvider.GetRequiredService<OutboxPoller>();
            claimed = await poller.ClaimAsync(CancellationToken.None);
        }

        foreach (var item in claimed)
        {
            await using var itemScope = Services.CreateAsyncScope();
            var processor = itemScope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>();
            await processor.ProcessAsync(item, CancellationToken.None);
        }
    }

    /// <summary>Forces a redelivery of an already-processed message, to exercise idempotency.</summary>
    public async Task RedeliverAsync(Guid orderId)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var row = await context.Outbox.SingleAsync(m => m.Payload.Contains(orderId.ToString()));

        var processor = scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>();
        await processor.ProcessAsync(
            new OutboxWorkItem(row.Id, row.EventName, row.Payload, row.Attempts + 1),
            CancellationToken.None);
    }
}
