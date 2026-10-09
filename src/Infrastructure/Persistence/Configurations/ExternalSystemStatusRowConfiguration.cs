using AiFramework.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class ExternalSystemStatusRowConfiguration : IEntityTypeConfiguration<ExternalSystemStatusRow>
{
    public void Configure(EntityTypeBuilder<ExternalSystemStatusRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("external_system_status");

        // The system name: 1-64 letters, digits or dashes, validated at startup (ExternalSystemsOptionsValidator).
        builder.HasKey(s => s.Name);
        builder.Property(s => s.Name).HasMaxLength(64);

        builder.Property(s => s.State).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(s => s.Description).HasMaxLength(ExternalSystemStatusStore.MaxDescriptionLength);
    }
}
