using System.Net.Http.Json;
using AiFramework.Api.IntegrationTests.Diagnostics;
using AiFramework.Domain.Users;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace AiFramework.Api.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // The parameterless PostgreSqlBuilder() + WithImage(...) pairing is obsolete in
    // Testcontainers.PostgreSql 4.14.0 (CS0618); the image now goes to the constructor.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    private readonly RabbitMqContainer _rabbit =
        new RabbitMqBuilder("rabbitmq:4.3-management-alpine").Build();

    /// <summary>For tests that inspect or publish to the broker directly (BrokerProbe).</summary>
    public string RabbitMqConnectionString => _rabbit.GetConnectionString();

    // The containers must start BEFORE anything touches Services: the first access to
    // Services builds the host, which runs ConfigureWebHost, which reads the containers'
    // connection strings. Reversing these two lines fails with a connection error.
    async Task IAsyncLifetime.InitializeAsync()
    {
        await Task.WhenAll(_container.StartAsync(), _rabbit.StartAsync());
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>()
            .Database.MigrateAsync();
    }

    // Explicit interface implementation: WebApplicationFactory already exposes a
    // ValueTask DisposeAsync() from IAsyncDisposable, so declaring xUnit's
    // Task DisposeAsync() implicitly would hide it and leak the host.
    //
    // The host goes BEFORE the containers, the mirror of InitializeAsync. The other way round,
    // durable Wolverine's DurabilityAgent keeps polling a database that is already gone and logs
    // each failure; on Windows one of those lands after the EventLog provider is disposed, and the
    // resulting ObjectDisposedException on a thread-pool thread crashes the test host after every
    // test has passed ("Test Run Aborted"). Whether it hit was down to scheduling. The broker
    // follows the same rule: a host outliving it would sit in its reconnect loop while stopping.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _container.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Program.cs now throws at startup when ConnectionStrings:Default is null or
        // whitespace (see Program.cs). appsettings.json ships "" for that key, so this
        // setting must be supplied before the host is built, not swapped in afterward via
        // ConfigureServices below — otherwise the stricter guard throws first.
        builder.UseSetting("ConnectionStrings:Default", _container.GetConnectionString());

        // Durable Wolverine now also connects to RabbitMQ while the host starts (ADR 0026), so the
        // broker, like the database, must be supplied before the host is built.
        builder.UseSetting("ConnectionStrings:RabbitMq", _rabbit.GetConnectionString());

        // CreateAuthenticatedClientAsync registers a fresh user for nearly every test in this
        // project, all from one address. The production limit would exhaust itself partway
        // through the run and fail tests that have nothing to do with rate limiting, so this
        // host sets it out of the way. The limiter's own behaviour is covered by
        // AuthRateLimitTests, which stands up its own host with a tiny limit.
        builder.UseSetting("RateLimiting:Auth:PermitLimit", "1000000");

        // Off for every test in this project, for the same reason the rate limit is raised above:
        // these tests were written against uncached reads, and a hit would make an unrelated
        // assertion fail as though the endpoint were broken. The cache's own behaviour is covered
        // by Orders/OrderCachingTests, which stays in ApiFactoryCollection and layers
        // WithWebHostBuilder over this factory with it switched on, rather than standing up its
        // own host — the same split AuthRateLimitTests uses for the limiter, but keeping the one
        // shared Postgres container.
        builder.UseSetting("Cache:Enabled", "false");

        // Belt and braces: appsettings.json already defaults this to false, so every test host
        // would get an unexported (but still active) tracer provider either way. This is
        // defence against someone flipping that default later — without it, every integration
        // test in this project would attempt OTLP delivery to a collector that is not there.
        builder.UseSetting("Observability:Otlp:Enabled", "false");

        // Off for every test in this project, like the cache above. appsettings.json already
        // defaults it to false, so this is defence against someone flipping that default: with
        // realtime on and no Redis, every notification write would additionally go through a
        // SignalR hub context these tests never read from. NotificationHubTests turns it back on
        // for itself, the same WithWebHostBuilder split OrderCachingTests uses.
        builder.UseSetting("Realtime:Enabled", "false");

        // Off for every test in this project, for the same reason the cache is off above: a
        // test asserting an ErrorKind.Unavailable failure must not first sit through the retry
        // pipeline's own backoff delays. RatesEndpointTests still exercises the real pipeline —
        // Resilience:Enabled only suppresses retrying (ADR 0014), not the request itself.
        builder.UseSetting("Resilience:Enabled", "false");

        // Port 1 on loopback: nothing is ever listening there, so the connection is refused
        // immediately by the kernel with no DNS lookup and no real wall-clock wait — the
        // opposite of pointing at the live provider, which this suite must never reach.
        // RatesEndpointTests proves the resulting 503 + Retry-After; no test here needs a
        // successful rate, so nothing more elaborate than "reliably unreachable" is required.
        builder.UseSetting("Resilience:ExchangeRateBaseAddress", "http://127.0.0.1:1");

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

    /// <summary>The password every client from <see cref="CreateAuthenticatedClientAsync"/> is
    /// registered with, exposed so a test can present it as the current password.</summary>
    public const string RegisteredPassword = "a long enough test password";

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
    public async Task<HttpClient> CreateAuthenticatedClientAsync() =>
        (await CreateAuthenticatedClientWithUsernameAsync()).Client;

    /// <summary>
    /// The same thing, plus the generated username, for tests that need a second session for the
    /// same user or want to reach that user in the database.
    /// </summary>
    public async Task<(HttpClient Client, string Username)> CreateAuthenticatedClientWithUsernameAsync()
    {
        var client = CreateClient();
        var username = $"u{Guid.NewGuid():N}"[..32];

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Username = username, Password = RegisteredPassword, DisplayName = "Test User" });

        response.EnsureSuccessStatusCode();

        return (client, username);
    }

    /// <summary>
    /// A second, independent signed-in client for an existing user. A distinct HttpClient means a
    /// distinct cookie container, which is what makes the two genuinely separate sessions.
    /// </summary>
    public async Task<HttpClient> SignInAgainAsync(string username)
    {
        var client = CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = username, Password = RegisteredPassword, RememberMe = false });

        response.EnsureSuccessStatusCode();

        return client;
    }

    /// <summary>
    /// Rotates a user's stamp directly, standing in for whatever would do it in production. Lets a
    /// test prove the validation path without depending on the endpoints that trigger it.
    /// </summary>
    public async Task RotateSecurityStampAsync(string username)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var normalized = User.Normalize(username);

        var user = await context.Users.SingleAsync(u => u.UsernameNormalized == normalized);
        user.RotateSecurityStamp();

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Sets a user's role directly, standing in for the administrator reconciler. Lets a test
    /// prove what a role change does to a LIVE session without going through configuration and a
    /// host restart, which is the only way production changes one.
    /// </summary>
    public async Task SetRoleAsync(string username, UserRole role)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var normalized = User.Normalize(username);

        var user = await context.Users.SingleAsync(u => u.UsernameNormalized == normalized);
        user.ChangeRole(role);

        await context.SaveChangesAsync();
    }

    /// <summary>Reads an account's id by username, for tests that act on somebody else.</summary>
    public async Task<Guid> GetUserIdAsync(string username)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var normalized = User.Normalize(username);

        return await context.Users
            .Where(u => u.UsernameNormalized == normalized)
            .Select(u => u.Id)
            .SingleAsync();
    }

    /// <summary>
    /// Reads a role straight from the database, so an assertion about a role change does not go
    /// back through the endpoint that performed it.
    /// </summary>
    public async Task<UserRole> GetRoleAsync(Guid userId)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        return await context.Users.Where(u => u.Id == userId).Select(u => u.Role).SingleAsync();
    }

    /// <summary>
    /// A signed-in client whose user holds <c>Admin</c>, for the monitoring endpoints. The role is
    /// read from the database on every request (ADR 0020), so promoting after sign-in needs no
    /// re-authentication — which is itself asserted by MonitoringAccessTests.
    /// </summary>
    public async Task<HttpClient> CreateAdminClientAsync()
    {
        var (client, username) = await CreateAuthenticatedClientWithUsernameAsync();
        await SetRoleAsync(username, UserRole.Admin);

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
