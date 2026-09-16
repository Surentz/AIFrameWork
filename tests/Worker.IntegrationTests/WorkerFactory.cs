using AiFramework.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace AiFramework.Worker.IntegrationTests;

/// <summary>
/// The real worker host against a real Postgres, mirroring <c>Api.IntegrationTests/ApiFactory</c>.
/// </summary>
/// <remarks>
/// The worker listens on real Postgres queues, so unlike the Api factory this one does NOT strip
/// its background machinery out — the listeners are the thing under test. What it does instead is
/// keep both lanes on, so a test can assert which queue a job actually landed on.
/// </remarks>
public sealed class WorkerFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    // The container must start BEFORE anything touches Services: the first access builds AND
    // STARTS the host, which reads the container's connection string. Reversing these two fails to
    // connect.
    //
    // Migration now happens against a STANDALONE DbContext, built directly off the container's
    // connection string, rather than by touching `Services` first and migrating from inside it —
    // the order that worked before Quartz joined this host. `AddJobScheduling` runs
    // SchemaProvisioning.Validate against the `quartz` schema and ScheduleSynchronizer reads the
    // `quartz.qrtz_*` tables, both during host START, and the first access to `Services` below is
    // exactly what builds and starts that host. Migrating only after that access — as this used to
    // — would ask Quartz to validate a schema that does not exist yet. Building a bare
    // AiFrameworkDbContext here, the same shape DesignTimeDbContextFactory uses for `dotnet ef`,
    // lets the migration run with no host and therefore no Quartz in the picture at all.
    async Task IAsyncLifetime.InitializeAsync()
    {
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<AiFrameworkDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        await using var context = new AiFrameworkDbContext(options);
        await context.Database.MigrateAsync();
    }

    // Explicit interface implementation: WebApplicationFactory already exposes a
    // ValueTask DisposeAsync() from IAsyncDisposable, so declaring xUnit's Task DisposeAsync()
    // implicitly would hide it and leak the host.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await _container.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Program.cs throws at startup when this is null or whitespace, and appsettings.json ships
        // "" for the key, so it must be supplied before the host is built rather than swapped in
        // afterwards — the same ordering constraint ApiFactory documents.
        builder.UseSetting("ConnectionStrings:Default", _container.GetConnectionString());

        // Both lanes, so a test can prove a Heavy job went to jobs_heavy and not jobs_light.
        builder.UseSetting("Jobs:Queues", "light,heavy");

        // Matches the deployed worker (k8s/base/worker.yaml sets Cache__Enabled=false) rather than
        // being a test convenience. ADR 0016: a job's eviction reaches only the worker's own empty
        // L1 cache, so caching here buys nothing and hides that fact.
        builder.UseSetting("Cache:Enabled", "false");

        builder.UseSetting("Observability:Otlp:Enabled", "false");

        // A test asserting a job's failure path must not first sit through the retry pipeline's
        // own backoff delays — the same reason ApiFactory sets it. ADR 0014.
        builder.UseSetting("Resilience:Enabled", "false");
    }
}

// CA1711: the name ends in "Collection" without implementing ICollection<T>. This is the xUnit
// collection-definition idiom — an empty marker type referenced only via nameof() — not a general
// collection type. Every class here joins this one collection so xUnit starts exactly one
// container and one worker host for the whole project, exactly as ApiFactoryCollection does.
#pragma warning disable CA1711
[CollectionDefinition(nameof(WorkerFactoryCollection))]
public sealed class WorkerFactoryCollection : ICollectionFixture<WorkerFactory>;
#pragma warning restore CA1711
