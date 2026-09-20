using AiFramework.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class TrafficBucketConfiguration : IEntityTypeConfiguration<TrafficBucket>
{
    public void Configure(EntityTypeBuilder<TrafficBucket> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("traffic_buckets");

        // All four, because a row is one pod's minute. Summing across InstanceId is what makes
        // two API replicas report the application rather than whichever pod answered.
        builder.HasKey(b => new { b.BucketStart, b.Kind, b.Name, b.InstanceId });

        builder.Property(b => b.Kind).IsRequired().HasMaxLength(16).HasConversion<string>();

        // Route templates and request type names are both short. Bounded because the HTTP key is
        // derived from routing rather than from anything a caller sends, but a cap costs nothing.
        builder.Property(b => b.Name).IsRequired().HasMaxLength(256);
        builder.Property(b => b.InstanceId).IsRequired().HasMaxLength(128);

        // The order every read uses: a trailing window, newest first.
        builder.HasIndex(b => b.BucketStart)
            .IsDescending()
            .HasDatabaseName("IX_TrafficBuckets_BucketStart_Desc");
    }
}
