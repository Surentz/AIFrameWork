using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiFramework.Infrastructure.Tests.Persistence;

/// <summary>
/// StoreOrderExportsAsBytes deletes Ready CSV exports (they cannot be served as PDFs) and keeps
/// Requested ones (their build writes bytes now). On a database of its own, because it migrates
/// down and up, which the shared fixture's database must never see.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class OrderExportMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task StoreOrderExportsAsBytes_DeletesReadyExportsAndKeepsRequestedOnes()
    {
        await using var context = fixture.CreateContextForNewDatabase($"migration_{Guid.NewGuid():N}");
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToList();
        var target = migrations.Single(m => m.EndsWith("_StoreOrderExportsAsBytes", StringComparison.Ordinal));
        await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1]);

        var ready = Guid.NewGuid();
        var requested = Guid.NewGuid();
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO order_exports ("Id", "UserId", "RequestedAt", "Status", "CompletedAt", "RowCount", "Content")
            VALUES ({ready}, {Guid.NewGuid()}, now(), 'Ready', now(), 1, 'OrderId'),
                   ({requested}, {Guid.NewGuid()}, now(), 'Requested', NULL, NULL, NULL)
            """);

        await migrator.MigrateAsync(target);

        var ids = await context.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM order_exports""")
            .ToListAsync();
        ids.Should().Equal(requested);
    }
}
