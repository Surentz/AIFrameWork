using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
