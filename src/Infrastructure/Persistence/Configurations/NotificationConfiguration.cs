using AiFramework.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.UserId).IsRequired();
        builder.Property(n => n.SourceMessageId).IsRequired();
        builder.Property(n => n.Title).IsRequired().HasMaxLength(Notification.MaxTitleLength);
        builder.Property(n => n.Body).IsRequired().HasMaxLength(Notification.MaxBodyLength);
        builder.Property(n => n.CreatedAt).IsRequired();

        // Stored as its NAME, not its number. An enum persisted as int silently reinterprets
        // every existing row the moment someone reorders the members; the names are already a
        // stated contract on NotificationKind itself.
        builder.Property(n => n.Kind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        // Transient state the interceptor drains before save — see OrderConfiguration for what
        // goes wrong without this.
        builder.Ignore(n => n.DomainEvents);

        // IsRead is computed from ReadAt and has no setter, so there is nothing to map. Mapping
        // it would add a column that duplicates ReadAt and could disagree with it.
        builder.Ignore(n => n.IsRead);

        ConfigureIndexes(builder);
    }

    /// <summary>
    /// Split out of <see cref="Configure"/> to stay under MA0051's line limit — the rule is
    /// satisfied rather than suppressed.
    /// </summary>
    private static void ConfigureIndexes(EntityTypeBuilder<Notification> builder)
    {
        // What actually makes the event handlers idempotent. The dedupe read in
        // NotificationFanOut is an optimization; THIS is the guarantee — two concurrent
        // deliveries of one message cannot both land a row for the same (message, recipient,
        // kind).
        //
        // Kind is part of the key, and leaving it out was a latent trap rather than a
        // simplification. One event may legitimately have more than one notifier (OrderPlaced
        // already carries two HANDLERS today). The moment a second notifier writes a different
        // KIND for the same recipient off the same message, a (SourceMessageId, UserId) key
        // makes them collide: one insert throws, the outbox retries, and the retry's dedupe
        // read then suppresses BOTH — losing the second notification permanently while the
        // outbox row reads Processed.
        builder.HasIndex(n => new { n.SourceMessageId, n.UserId, n.Kind })
            .IsUnique()
            .HasDatabaseName("IX_Notifications_SourceMessageId_UserId_Kind");

        // Both of the next two cover the SAME property set, so they MUST use the named
        // HasIndex overload. EF identifies an index by its properties: called unnamed, the second
        // call reconfigures the first rather than adding to it, and the filtered one silently
        // replaces the plain one — which the generated migration then reports as a single index,
        // leaving the main feed query with nothing to seek on. Caught exactly that way here.

        // The feed: filter by owner, order by (CreatedAt DESC, Id DESC). Owner leads because it
        // is the equality predicate, matching IX_Orders_UserId_PlacedAt_Id_Desc's reasoning.
        builder.HasIndex(
                n => new { n.UserId, n.CreatedAt, n.Id },
                "IX_Notifications_UserId_CreatedAt_Id_Desc")
            .IsDescending(false, true, true);

        // The badge count and the unread-only feed, both of which filter on ReadAt IS NULL.
        // Partial rather than covering every row: read notifications accumulate without bound and
        // are never the subject of this query, so indexing them is pure write cost. The predicate
        // is written to match NotificationRepository's `n.ReadAt == null` exactly — Postgres only
        // uses a partial index when it can prove the query implies the index's own filter.
        builder.HasIndex(
                n => new { n.UserId, n.CreatedAt, n.Id },
                "IX_Notifications_Unread")
            .IsDescending(false, true, true)
            .HasFilter("\"ReadAt\" IS NULL");
    }
}
