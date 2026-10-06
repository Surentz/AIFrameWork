using AiFramework.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class OrderExportConfiguration : IEntityTypeConfiguration<OrderExport>
{
    public void Configure(EntityTypeBuilder<OrderExport> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("order_exports");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.UserId).IsRequired();
        builder.Property(e => e.RequestedAt).IsRequired();

        // bytea: the built file. Only GetFileAsync ever selects it; the list projects it away.

        // By name, like OrderStatus and NotificationKind: OrderExportStatus says the names are
        // the stored contract.
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16);

        // Transient state the outbox interceptor drains before save.
        builder.Ignore(e => e.DomainEvents);

        // Optimistic concurrency, the way OrderConfiguration does it: Postgres's xmin, which adds
        // no column. Complete is a no-op on a Ready export, but that only guards a second build
        // that runs after the first committed. Two that overlap both read Requested, and without
        // this both would save and two "ready" notifications would go out. With it the loser's
        // save fails, CompleteOrderExport answers Conflict, the job throws and is retried, and the
        // retry finds the export Ready and stops.
        builder.Property<uint>(OrderConfiguration.RowVersion).IsRowVersion();

        // Every read is "this owner's exports, newest first" — the list, and the in-progress check
        // a request makes. No foreign key to users: no table here declares one (see
        // NotificationConfiguration), and an export outlives nothing it would protect.
        builder.HasIndex(e => new { e.UserId, e.RequestedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_OrderExports_UserId_RequestedAt");
    }
}
