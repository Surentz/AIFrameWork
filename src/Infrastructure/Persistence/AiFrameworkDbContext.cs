using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class AiFrameworkDbContext(DbContextOptions<AiFrameworkDbContext> options)
    : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AiFrameworkDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
