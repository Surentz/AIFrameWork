using AiFramework.Infrastructure.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class AssistantTurnRowConfiguration : IEntityTypeConfiguration<AssistantTurnRow>
{
    public void Configure(EntityTypeBuilder<AssistantTurnRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("assistant_turns");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Seq).IsRequired();
        builder.Property(t => t.Kind).IsRequired().HasMaxLength(16);

        // text, not jsonb: replayed to the provider exactly as stored.
        builder.Property(t => t.Payload).IsRequired().HasColumnType("text");
        builder.Property(t => t.CreatedAt).IsRequired();

        // Not unique, deliberately: a race between two questions must surface as the
        // conversation's xmin conflict (a 409), never as a unique violation here (a 500).
        builder.HasIndex(t => new { t.ConversationId, t.Seq }).HasDatabaseName("IX_AssistantTurns_ConversationId_Seq");

        builder.HasOne<AssistantConversationRow>()
            .WithMany()
            .HasForeignKey(t => t.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
