using AiFramework.Domain.Notifications;
using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;
using AiFramework.Domain.Users;
using AiFramework.Infrastructure.Jobs;
using AiFramework.Infrastructure.Monitoring;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class AiFrameworkDbContext(DbContextOptions<AiFrameworkDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<OrderAudit> OrderAudits => Set<OrderAudit>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<JobRun> JobRuns => Set<JobRun>();

    public DbSet<SignInEvent> SignInEvents => Set<SignInEvent>();

    public DbSet<TrafficBucket> TrafficBuckets => Set<TrafficBucket>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AiFrameworkDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
