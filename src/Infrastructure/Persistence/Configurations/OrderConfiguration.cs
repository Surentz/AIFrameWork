using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;
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

        // By NAME, not by number, like NotificationKind — an enum stored as int silently
        // reinterprets every existing row the moment someone reorders the members. OrderStatus
        // says the names are the stored contract; this line is what makes that true.
        // HasDefaultValue is load-bearing for the BACKFILL, not for inserts. Without it the
        // generated migration adds this NOT NULL column with defaultValue: "" — every order that
        // predates the column gets an empty string, which then fails to convert back to the enum
        // on read, breaking every order query against rows that were perfectly fine before.
        // Placed is what those rows always were; see OrderStatus on why it is first.
        builder.Property(o => o.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(OrderStatus.Placed);

        builder.Property(o => o.ShippedAt);
        builder.Property(o => o.CancelledAt);
        builder.Property(o => o.CancellationReason)
            .HasMaxLength(Order.MaxCancellationReasonLength);

        // DomainEvents is transient state the interceptor drains before save; it is not
        // persisted. Without this, EF tries to map IDomainEvent as an entity type and the
        // model fails to build at first use.
        builder.Ignore(o => o.DomainEvents);

        // Owned rather than three loose nullable scalars, so "all three columns or none" is
        // structural instead of a convention this class has to police. EF decides the dependent
        // is absent by looking at its REQUIRED properties - OrderedProduct's three are all
        // non-nullable CLR types - which is why this does not trip
        // OptionalDependentWithAllNullPropertiesWarning, and why adding a nullable property to
        // OrderedProduct later would.
        builder.OwnsOne(o => o.Product, product =>
        {
            product.Property(p => p.ProductId).HasColumnName("ProductId");
            product.Property(p => p.Name)
                .HasColumnName("ProductName")
                .HasMaxLength(Product.MaxNameLength);
            product.Property(p => p.UnitPrice).HasColumnName("UnitPrice").HasPrecision(18, 2);
        });

        // Rows written before the catalogue link have all three columns null.
        builder.Navigation(o => o.Product).IsRequired(false);

        ConfigureIndexes(builder);
    }

    /// <summary>
    /// Split out of <see cref="Configure"/> to stay under MA0051's line limit — the rule is
    /// satisfied rather than suppressed.
    /// </summary>
    private static void ConfigureIndexes(EntityTypeBuilder<Order> builder)
    {
        // The list endpoint filters by owner and orders by (PlacedAt DESC, Id DESC). The owner
        // is the leading column because it is an equality predicate; the previous
        // IX_Orders_PlacedAt_Id_Desc cannot serve this query and is dropped.
        builder.HasIndex(o => new { o.UserId, o.PlacedAt, o.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_Orders_UserId_PlacedAt_Id_Desc");

        // ListPurchaserIdsAsync — the recipient rule for a price change — filters on Sku and
        // groups by UserId. The index above leads with UserId, so it cannot serve that query at
        // all: without this one it is a sequential scan plus aggregate over the whole table, run
        // on the outbox pump once per ProductPriceChanged delivery. PlacedAt is included because
        // that query also takes Max(PlacedAt) per user to order the fan-out.
        builder.HasIndex(o => new { o.Sku, o.UserId, o.PlacedAt })
            .HasDatabaseName("IX_Orders_Sku_UserId_PlacedAt");
    }
}
