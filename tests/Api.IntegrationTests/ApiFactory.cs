using System.Net.Http.Json;
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

        // CreateAuthenticatedClientAsync registers a fresh user for nearly every test in this
        // project, all from one address. The production limit would exhaust itself partway
        // through the run and fail tests that have nothing to do with rate limiting, so this
        // host sets it out of the way. The limiter's own behaviour is covered by
        // AuthRateLimitTests, which stands up its own host with a tiny limit.
        builder.UseSetting("RateLimiting:Auth:PermitLimit", "1000000");

        // Off for every test in this project, for the same reason the rate limit is raised above:
        // these tests were written against uncached reads, and a hit would make an unrelated
        // assertion fail as though the endpoint were broken. The cache's own behaviour is covered
        // by Orders/OrderCachingTests, which stands up its own host with it switched on — the
        // same split AuthRateLimitTests uses for the limiter.
        builder.UseSetting("Cache:Enabled", "false");

        // Split out to a method of its own so ConfigureWebHost stays under MA0051's line limit
        // now that it also carries the Cache:Enabled setting above — the split is purely
        // mechanical, the ConfigureServices callback itself is unchanged.
        builder.ConfigureServices(ConfigureServicesForTests);
    }

    private static void ConfigureServicesForTests(IServiceCollection services)
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
        // DrainOutboxUntilEmptyAsync for the same rows and make outbox tests timing-dependent.
        // The drain helper below invokes the same OutboxPoller and OutboxWorkItemProcessor
        // the pumps use, so the wiring under test is still the real one.
        //
        // This must be a narrowed removal, not a blanket one keyed only on the
        // IHostedService service type: the generic host appends its own hosted service for
        // GenericWebHostService — the piece that actually starts the server and builds the
        // request pipeline — after user ConfigureServices callbacks run. Removing every
        // IHostedService descriptor here would delete that one too; it only happens to work
        // today because of that registration order, and moving this removal to a later hook
        // (e.g. ConfigureTestServices) would silently stop the test host from serving
        // requests. Filtering by ImplementationType keeps this immune to that ordering
        // accident.
        var outboxHostedServices = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && (descriptor.ImplementationType == typeof(OutboxPollerService)
                    || descriptor.ImplementationType == typeof(OutboxWorkerService)))
            .ToList();
        foreach (var descriptor in outboxHostedServices)
        {
            services.Remove(descriptor);
        }

        // Test-only endpoints that throw on demand, exercising GlobalExceptionHandler's
        // two branches over real HTTP. See TestEndpointsStartupFilter for why this is an
        // IStartupFilter rather than a controller.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, TestEndpointsStartupFilter>());
    }

    /// <summary>
    /// A client that has registered a fresh user and is carrying its session cookie, for the
    /// endpoints that now require one. <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>
    /// handles cookies by default, so every later request on the returned client is signed in.
    /// </summary>
    /// <remarks>
    /// A fresh user per client rather than one shared account: these tests all share one database
    /// through ApiFactoryCollection, and a shared user would let a password change in one test
    /// invalidate another's session.
    /// </remarks>
    public async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                Username = $"u{Guid.NewGuid():N}"[..32],
                Password = "a long enough test password",
                DisplayName = "Test User",
            });

        response.EnsureSuccessStatusCode();

        return client;
    }

    /// <summary>
    /// Drains every outbox row currently due — not just one batch. ClaimAsync's query is
    /// <c>LIMIT BatchSize</c> (see OutboxOptions), so a single claim only ever picks up the
    /// first BatchSize due rows; now that every Api.IntegrationTests class shares one database
    /// via ApiFactoryCollection, rows from earlier tests in the run can sit ahead of the row a
    /// later test cares about and nothing prunes between tests. This loops claim-and-process
    /// cycles until a claim comes back empty, so the queue really is empty when this returns,
    /// regardless of what other tests left behind. Drives the same OutboxPoller and
    /// OutboxWorkItemProcessor the hosted services use, so the wiring under test is real — but
    /// deterministically, without waiting on BackgroundService timing. Each claimed item gets
    /// its own scope, mirroring OutboxWorkerService.ProcessOneAsync: production never shares one
    /// DbContext across items, so neither does this. What this does NOT cover: the channel hop
    /// (ChannelWriter to ChannelReader), backpressure, and WorkerCount parallelism — those are
    /// exercised by Infrastructure.Tests/Outbox/OutboxRegistrationTests.cs instead.
    /// This is bounded synchronous draining of rows already in the database, not polling for a
    /// background state change, so looping here does not violate tests/CLAUDE.md's no-sleep
    /// rule — keep it that way: no Thread.Sleep, no Task.Delay, no retry-until-timeout. The
    /// MaxBatches cap is a safety net so a bug that keeps producing due rows fails loudly
    /// instead of hanging CI.
    /// </summary>
    public async Task DrainOutboxUntilEmptyAsync()
    {
        const int MaxBatches = 1_000;

        for (var batch = 0; batch < MaxBatches; batch++)
        {
            IReadOnlyList<OutboxWorkItem> claimed;
            await using (var pollScope = Services.CreateAsyncScope())
            {
                var poller = pollScope.ServiceProvider.GetRequiredService<OutboxPoller>();
                claimed = await poller.ClaimAsync(CancellationToken.None);
            }

            if (claimed.Count == 0)
            {
                return;
            }

            foreach (var item in claimed)
            {
                await using var itemScope = Services.CreateAsyncScope();
                var processor = itemScope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>();
                await processor.ProcessAsync(item, CancellationToken.None);
            }
        }

        throw new InvalidOperationException(
            $"DrainOutboxUntilEmptyAsync claimed {MaxBatches} batches without the outbox emptying; " +
            "the queue is likely growing faster than it drains, or ClaimAsync is not converging.");
    }

    /// <summary>
    /// Forces a redelivery of an already-processed message, to exercise idempotency. Resets the
    /// row to the state DomainEventsInterceptor first inserts it in — Pending, NextAttemptAt
    /// null, LeasedUntil null — except Attempts, which is deliberately left carried over from
    /// the first delivery. It then runs the row back through DrainOutboxUntilEmptyAsync, so the
    /// redelivery is claimed by the real ClaimAsync rather than handed to the processor as a
    /// hand-built OutboxWorkItem the poller could never actually produce (a Processed row is
    /// never re-claimable). ExecuteUpdateAsync targets the row directly by predicate, so there is
    /// no tracked (or untracked) read to reconcile with src/Infrastructure/CLAUDE.md's
    /// AsNoTracking rule here.
    /// </summary>
    public async Task RedeliverAsync(Guid orderId)
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
            var resetCount = await context.Outbox
                .Where(m => m.Payload.Contains(orderId.ToString()))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxStatus.Pending)
                    .SetProperty(m => m.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LeasedUntil, (DateTimeOffset?)null));

            if (resetCount != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one outbox row for order '{orderId}' to reset for redelivery, found {resetCount}.");
            }
        }

        await DrainOutboxUntilEmptyAsync();
    }
}

// CA1711: the name ends in "Collection" without implementing ICollection<T>. This is the
// xUnit collection-definition idiom - an empty marker type named after the fixture it groups,
// referenced only via nameof() in [Collection(nameof(ApiFactoryCollection))] - not a general
// collection type, so the rule's intent (avoid confusing a type for a collection API) does not
// apply here. Mirrors PostgresFixture's ICollectionFixture<T> pattern in
// tests/Infrastructure.Tests/Persistence/PostgresFixture.cs: every Api.IntegrationTests class
// that needs an ApiFactory joins this one collection instead of declaring its own
// IClassFixture<ApiFactory>, so xUnit starts exactly one container/host for the whole project.
#pragma warning disable CA1711
[CollectionDefinition(nameof(ApiFactoryCollection))]
public sealed class ApiFactoryCollection : ICollectionFixture<ApiFactory>;
#pragma warning restore CA1711
