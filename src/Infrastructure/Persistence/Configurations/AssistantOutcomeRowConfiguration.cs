using AiFramework.Infrastructure.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class AssistantOutcomeRowConfiguration : IEntityTypeConfiguration<AssistantOutcomeRow>
{
    public void Configure(EntityTypeBuilder<AssistantOutcomeRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("assistant_outcomes");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Path).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(o => o.TargetId).HasMaxLength(64);
        builder.Property(o => o.Scope).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(o => o.At).IsRequired();

        // The insights page reads by time.
        builder.HasIndex(o => o.At).HasDatabaseName("IX_AssistantOutcomes_At");

        builder.HasOne<AssistantConversationRow>()
            .WithMany()
            .HasForeignKey(o => o.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
