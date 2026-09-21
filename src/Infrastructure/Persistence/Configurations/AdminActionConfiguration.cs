using AiFramework.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class AdminActionConfiguration : IEntityTypeConfiguration<AdminAction>
{
    public void Configure(EntityTypeBuilder<AdminAction> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("admin_actions");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.At).IsRequired();

        // As its NAME, like every other enum here: as an integer, reordering a member silently
        // changes what every existing row means.
        builder.Property(a => a.Kind).IsRequired().HasMaxLength(32).HasConversion<string>();

        builder.Property(a => a.ActorUserId).IsRequired();
        builder.Property(a => a.TargetUserId).IsRequired();

        // No foreign keys to users, deliberately. The audit outlives whatever happens to either
        // account, and a cascade that deleted history along with a row would defeat the purpose.
        // The usernames are stored alongside the ids for the same reason.
        builder.Property(a => a.ActorUsername)
            .IsRequired()
            .HasMaxLength(Domain.Users.User.MaxUsernameLength);

        builder.Property(a => a.TargetUsername)
            .IsRequired()
            .HasMaxLength(Domain.Users.User.MaxUsernameLength);

        builder.Property(a => a.IpAddress).HasMaxLength(64);
        builder.Property(a => a.UserAgent).HasMaxLength(512);
        builder.Property(a => a.TraceId).HasMaxLength(64);

        // The two orders this is read in: the newest changes overall, and the history of one
        // account. The second is what makes "what has been done to this user" a seek.
        builder.HasIndex(a => a.At)
            .IsDescending()
            .HasDatabaseName("IX_AdminActions_At_Desc");

        builder.HasIndex(a => new { a.TargetUserId, a.At })
            .IsDescending(false, true)
            .HasDatabaseName("IX_AdminActions_TargetUserId_At_Desc");
    }
}
