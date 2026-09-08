using AiFramework.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Username).IsRequired().HasMaxLength(User.MaxUsernameLength);
        builder.Property(u => u.UsernameNormalized).IsRequired().HasMaxLength(User.MaxUsernameLength);

        // 256 is slack, not a measurement: PasswordHasher's PBKDF2 format is a version byte plus
        // a 128-bit salt and a 256-bit subkey, base64'd - about 84 characters. The column has to
        // outlive a future format change, and a wider varchar costs nothing in Postgres.
        builder.Property(u => u.PasswordHash).IsRequired().HasMaxLength(256);
        builder.Property(u => u.DisplayName).IsRequired().HasMaxLength(User.MaxDisplayNameLength);
        builder.Property(u => u.RegisteredAt).IsRequired();

        builder.Property(u => u.FailedSignInAttempts).IsRequired();

        // Nullable by design: null means "not locked", which is a different state from "locked
        // until a time in the past" and avoids a sentinel date.
        builder.Property(u => u.LockedOutUntil);

        // DomainEvents is transient state the interceptor drains before save; it is not
        // persisted. Without this, EF tries to map IDomainEvent as an entity type and the
        // model fails to build at first use.
        builder.Ignore(u => u.DomainEvents);

        // The uniqueness guarantee, on the case-folded column rather than on Username: without
        // it "Ada" and "ada" are two accounts, and the check in RegisterUserHandler is only a
        // friendly pre-check - two simultaneous registrations both pass it.
        builder.HasIndex(u => u.UsernameNormalized)
            .IsUnique()
            .HasDatabaseName("IX_Users_UsernameNormalized");
    }
}
