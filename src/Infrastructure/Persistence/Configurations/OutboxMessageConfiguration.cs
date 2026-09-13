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

        // Nullable: a row written before this column existed has none, and a row raised with no
        // ambient Activity legitimately has none either — neither is an error. A W3C traceparent
        // under the default (version 00) format is exactly 55 characters
        // ("00-" + 32 hex + "-" + 16 hex + "-" + 2 hex); the slack to 64 is the same margin
        // User.MaxSecurityStampLength gives a fixed-shape hex value, for a future format change.
        builder.Property(m => m.TraceParent).HasMaxLength(64);

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
