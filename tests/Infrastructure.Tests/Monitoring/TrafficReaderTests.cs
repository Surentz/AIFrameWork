using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Monitoring;
using AiFramework.Infrastructure.Persistence;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Monitoring;

/// <summary>
/// What the traffic page's reader includes. The numbers are the application's own work, so the
/// outbound kinds must not leak in. See ADR 0031.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class TrafficReaderTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    [Fact]
    public async Task SummarizeAsync_IgnoresOutboundKinds()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-10);
        var minute = new DateTimeOffset(since.Year, since.Month, since.Day, since.Hour, since.Minute, 0, TimeSpan.Zero)
            .AddMinutes(5);
        var instance = $"pod-{Guid.NewGuid():N}";
        await using (var context = _fixture.CreateContext())
        {
            context.TrafficBuckets.AddRange(
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Http, Name = "GET /api/orders", InstanceId = instance, Succeeded = 3 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Outbound, Name = "Sim", InstanceId = instance, Faulted = 7 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.OutboundAttempt, Name = "Sim", InstanceId = instance, Faulted = 21 });
            await context.SaveChangesAsync();
        }

        await using var reading = _fixture.CreateContext();
        var summary = await new TrafficReader(reading).SummarizeAsync(since, CancellationToken.None);

        summary.Overall.Total.Should().Be(3);
        summary.Rows.Should().OnlyContain(row => row.Kind == TrafficKind.Http);
    }

    [Fact]
    public async Task SeriesAsync_IgnoresOutboundKinds()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-20);
        var minute = new DateTimeOffset(since.Year, since.Month, since.Day, since.Hour, since.Minute, 0, TimeSpan.Zero)
            .AddMinutes(7);
        var instance = $"pod-{Guid.NewGuid():N}";
        await using (var context = _fixture.CreateContext())
        {
            context.TrafficBuckets.AddRange(
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Http, Name = "GET /api/orders", InstanceId = instance, Succeeded = 2 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Outbound, Name = "Sim", InstanceId = instance, Faulted = 9 });
            await context.SaveChangesAsync();
        }

        await using var reading = _fixture.CreateContext();
        var series = await new TrafficReader(reading).SeriesAsync(since, CancellationToken.None);

        series.Points.Should().ContainSingle(point => point.BucketStart == minute).Which.Total.Should().Be(2);
    }
}
