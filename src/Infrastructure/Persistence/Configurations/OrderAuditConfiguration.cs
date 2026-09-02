using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class OrderAuditConfiguration : IEntityTypeConfiguration<OrderAudit>
{
    public void Configure(EntityTypeBuilder<OrderAudit> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("order_audit");
        builder.HasKey(a => a.MessageId);
        builder.Property(a => a.OrderId).IsRequired();
    }
}
