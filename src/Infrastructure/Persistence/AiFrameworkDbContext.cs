using AiFramework.Domain.Orders;
using AiFramework.Domain.Users;
using AiFramework.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class AiFrameworkDbContext(DbContextOptions<AiFrameworkDbContext> options)
    : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<OrderAudit> OrderAudits => Set<OrderAudit>();

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AiFrameworkDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
