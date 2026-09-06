using AiFramework.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("orders");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.UserId).IsRequired();
        builder.Property(o => o.Sku).IsRequired().HasMaxLength(64);
        builder.Property(o => o.Quantity).IsRequired();
        builder.Property(o => o.PlacedAt).IsRequired();

        // DomainEvents is transient state the interceptor drains before save; it is not
        // persisted. Without this, EF tries to map IDomainEvent as an entity type and the
        // model fails to build at first use.
        builder.Ignore(o => o.DomainEvents);

        // The list endpoint filters by owner and orders by (PlacedAt DESC, Id DESC). The owner
        // is the leading column because it is an equality predicate; the previous
        // IX_Orders_PlacedAt_Id_Desc cannot serve this query and is dropped.
        builder.HasIndex(o => new { o.UserId, o.PlacedAt, o.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_Orders_UserId_PlacedAt_Id_Desc");
    }
}
