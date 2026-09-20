using AiFramework.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class JobRunConfiguration : IEntityTypeConfiguration<JobRun>
{
    public void Configure(EntityTypeBuilder<JobRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("job_runs");

        // Composite, not a surrogate: Wolverine's message id is stable across retries, so the
        // pair is what makes a redelivery idempotent while keeping each attempt its own row.
        builder.HasKey(r => new { r.EnvelopeId, r.Attempt });

        builder.Property(r => r.JobName).IsRequired().HasMaxLength(128);

        // Both enums stored as their NAMES, like User.Role and for the same reason: as integers,
        // reordering a member silently changes what every existing row means.
        builder.Property(r => r.Lane).HasMaxLength(16).HasConversion<string>();
        builder.Property(r => r.Status).IsRequired().HasMaxLength(16).HasConversion<string>();

        builder.Property(r => r.StartedAt).IsRequired();

        // Deliberately unbounded: an exception message plus its type name has no sensible limit,
        // and truncating the one field an operator is reading would defeat the point.
        builder.Property(r => r.Error);

        builder.Property(r => r.TraceId).HasMaxLength(64);
        builder.Property(r => r.InstanceId).HasMaxLength(128);

        // The two orders the page reads in: the newest runs, and the newest runs of one status.
        builder.HasIndex(r => r.StartedAt)
            .IsDescending()
            .HasDatabaseName("IX_JobRuns_StartedAt_Desc");

        builder.HasIndex(r => new { r.Status, r.StartedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_JobRuns_Status_StartedAt_Desc");
    }
}
