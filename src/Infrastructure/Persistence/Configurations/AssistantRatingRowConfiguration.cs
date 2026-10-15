using AiFramework.Infrastructure.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class AssistantRatingRowConfiguration : IEntityTypeConfiguration<AssistantRatingRow>
{
    public void Configure(EntityTypeBuilder<AssistantRatingRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("assistant_ratings");
        builder.HasKey(r => new { r.ConversationId, r.Turn });
        builder.Property(r => r.Rating).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(r => r.RatedAt).IsRequired();

        // Gone with the conversation: the owner's delete and the 30-day retention remove it too.
        builder.HasOne<AssistantConversationRow>()
            .WithMany()
            .HasForeignKey(r => r.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
