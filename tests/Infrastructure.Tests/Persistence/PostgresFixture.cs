using AiFramework.Application.Abstractions;
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AiFramework.Infrastructure.Tests.Persistence;

public sealed class PostgresFixture : IAsyncLifetime
{
    // The parameterless PostgreSqlBuilder() + WithImage(...) pairing is obsolete in
    // Testcontainers.PostgreSql 4.14.0 (CS0618); the image now goes to the constructor.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public AiFrameworkDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AiFrameworkDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new AiFrameworkDbContext(options);
    }

    /// <summary>A context with the domain-events interceptor attached, as production has it.</summary>
    /// <summary>
    /// A context on a fresh, empty database in the same container, for tests that migrate down and
    /// up. The database is created by the first migration; nothing else shares it.
    /// </summary>
    public AiFrameworkDbContext CreateContextForNewDatabase(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = databaseName };
        return new AiFrameworkDbContext(new DbContextOptionsBuilder<AiFrameworkDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options);
    }

    public AiFrameworkDbContext CreateContextWithOutbox()
    {
        var services = new ServiceCollection();
        services.AddDomainEvent<OrderPlaced>("order.placed");
        services.AddDomainEvent<OrderExportRequested>("order_export.requested");
        services.AddDomainEvent<OrderExportCompleted>("order_export.completed");
        services.AddSingleton<DomainEventRegistry>();
        services.AddSingleton<IClock, SystemClock>();
        using var provider = services.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<AiFrameworkDbContext>()
            .UseNpgsql(ConnectionString)
            .AddInterceptors(new DomainEventsInterceptor(
                provider.GetRequiredService<DomainEventRegistry>(),
                provider.GetRequiredService<IClock>()))
            .Options;

        return new AiFrameworkDbContext(options);
    }
}

// CA1711: the name ends in "Collection" without implementing ICollection<T>. This is the
// xUnit collection-definition idiom - an empty marker type named after the fixture it groups,
// referenced only via nameof() in [Collection(nameof(PostgresCollection))] - not a general
// collection type, so the rule's intent (avoid confusing a type for a collection API) does
// not apply here.
#pragma warning disable CA1711
[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
#pragma warning restore CA1711
