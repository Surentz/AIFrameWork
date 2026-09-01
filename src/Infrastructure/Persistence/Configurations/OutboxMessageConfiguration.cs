using AiFramework.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.EventName).IsRequired().HasMaxLength(128);
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.OccurredAt).IsRequired();

        // Persisted as a string, not an ordinal: the claim query in OutboxPoller is raw SQL
        // comparing against 'Pending' and 'InFlight'. An ordinal column would match nothing
        // and the poller would spin against a full table. A string also survives a reorder
        // of the enum members.
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Property(m => m.LastError).HasMaxLength(2048);

        builder.HasIndex(m => new { m.Status, m.NextAttemptAt })
            .HasDatabaseName("ix_outbox_pending");
    }
}
