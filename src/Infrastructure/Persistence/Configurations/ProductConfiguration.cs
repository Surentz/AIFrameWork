using AiFramework.Domain.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("products");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Sku).IsRequired().HasMaxLength(Product.MaxSkuLength);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(Product.MaxNameLength);
        builder.Property(p => p.Description).HasMaxLength(Product.MaxDescriptionLength);

        // Scale 2 matches Product.PriceScale, which the domain enforces on the way in. Keep the
        // two in step: widening only the column would let a third decimal reach Postgres and be
        // rounded away silently, and narrowing only the domain would reject values the column
        // can hold perfectly well.
        builder.Property(p => p.Price).IsRequired().HasPrecision(18, Product.PriceScale);

        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        // DomainEvents is transient state the interceptor drains before save; it is not
        // persisted. Without this, EF tries to map IDomainEvent as an entity type and the
        // model fails to build at first use. Product raises no events today, but the property
        // is inherited from Entity and EF sees it regardless.
        builder.Ignore(p => p.DomainEvents);

        // The real guard behind CreateProductHandler's check-then-insert: two concurrent creates
        // of one sku both pass that check, and this index is what stops the second being written.
        // Unique on the stored value directly, because Product.Create normalizes before
        // constructing - there is no separate "as typed" column here, unlike User.
        builder.HasIndex(p => p.Sku).IsUnique().HasDatabaseName("IX_Products_Sku");

        // The list endpoint orders by (CreatedAt DESC, Id DESC) with no equality predicate in
        // front of it, because the catalogue is global - so unlike IX_Orders_UserId_PlacedAt_Id_Desc
        // there is no owner column leading this one.
        builder.HasIndex(p => new { p.CreatedAt, p.Id })
            .IsDescending(true, true)
            .HasDatabaseName("IX_Products_CreatedAt_Id_Desc");
    }
}
