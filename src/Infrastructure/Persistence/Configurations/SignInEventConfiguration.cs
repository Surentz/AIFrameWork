using AiFramework.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class SignInEventConfiguration : IEntityTypeConfiguration<SignInEvent>
{
    public void Configure(EntityTypeBuilder<SignInEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sign_in_events");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.At).IsRequired();

        // Nullable: an attempt against a username that does not exist has no account to point at,
        // which is exactly the case the attempted username below exists to keep investigable.
        builder.Property(e => e.UserId);

        builder.Property(e => e.UsernameAttempted)
            .IsRequired()
            .HasMaxLength(Domain.Users.User.MaxUsernameLength);

        // As its NAME, like every other enum here: as an integer, reordering a member silently
        // changes what every existing row means.
        builder.Property(e => e.Outcome).IsRequired().HasMaxLength(32).HasConversion<string>();

        builder.Property(e => e.IpAddress).HasMaxLength(64);
        builder.Property(e => e.UserAgent).HasMaxLength(512);
        builder.Property(e => e.TraceId).HasMaxLength(64);

        // The two orders the page reads in: the newest attempts, and the newest of one outcome.
        builder.HasIndex(e => e.At)
            .IsDescending()
            .HasDatabaseName("IX_SignInEvents_At_Desc");

        builder.HasIndex(e => new { e.Outcome, e.At })
            .IsDescending(false, true)
            .HasDatabaseName("IX_SignInEvents_Outcome_At_Desc");
    }
}
