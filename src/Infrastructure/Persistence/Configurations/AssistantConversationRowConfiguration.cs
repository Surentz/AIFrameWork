using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class AssistantConversationRowConfiguration : IEntityTypeConfiguration<AssistantConversationRow>
{
    /// <summary>The shadow property holding the row's xmin, as OrderConfiguration.RowVersion does.</summary>
    internal const string RowVersion = "RowVersion";

    public void Configure(EntityTypeBuilder<AssistantConversationRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("assistant_conversations");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.OwnerId).IsRequired();
        builder.Property(c => c.Title).HasMaxLength(AssistantTitle.MaxLength).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.LastActiveAt).IsRequired();
        builder.Property(c => c.QuestionCount).IsRequired();
        builder.Property(c => c.TurnCount).IsRequired();

        // Two questions racing in one conversation both read the same xmin; the second UPDATE
        // matches no row and Behaviors.CommitAsync turns that into a 409, rolling back its turns.
        builder.Property<uint>(RowVersion).IsRowVersion();

        // The hourly prune's predicate.
        builder.HasIndex(c => c.LastActiveAt).HasDatabaseName("IX_AssistantConversations_LastActiveAt");

        // The sidebar's query: one owner's conversations, newest first.
        builder.HasIndex(c => new { c.OwnerId, c.LastActiveAt })
            .HasDatabaseName("IX_AssistantConversations_OwnerId_LastActiveAt");
    }
}
