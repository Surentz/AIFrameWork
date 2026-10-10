using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Monitoring;
using AiFramework.Infrastructure.Persistence;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Monitoring;

/// <summary>
/// What the traffic page's reader includes. The numbers are the application's own work, so the
/// outbound kinds must not leak in. See ADR 0031.
/// </summary>
/// <remarks>
/// Each test owns a fixed minute no other test writes to, and asserts on its own unique names:
/// the reader has no upper bound, so a global total would depend on what else shares the database.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class TrafficReaderTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    [Fact]
    public async Task SummarizeAsync_IgnoresOutboundKinds()
    {
        var minute = new DateTimeOffset(2026, 8, 10, 12, 5, 0, TimeSpan.Zero);
        var instance = $"pod-{Guid.NewGuid():N}";
        var inbound = $"GET /api/orders-{Guid.NewGuid():N}";
        var system = $"Sim-{Guid.NewGuid():N}";
        await using (var context = _fixture.CreateContext())
        {
            context.TrafficBuckets.AddRange(
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Http, Name = inbound, InstanceId = instance, Succeeded = 3 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Outbound, Name = system, InstanceId = instance, Faulted = 7 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.OutboundAttempt, Name = system, InstanceId = instance, Faulted = 21 });
            await context.SaveChangesAsync();
        }

        await using var reading = _fixture.CreateContext();
        var summary = await new TrafficReader(reading).SummarizeAsync(minute.AddMinutes(-1), CancellationToken.None);

        summary.Rows.Should().ContainSingle(row => row.Name == inbound).Which.Total.Should().Be(3);
        summary.Rows.Should().NotContain(row => row.Name == system);
        summary.Rows.Should().OnlyContain(row => row.Kind == TrafficKind.Http);
        summary.Overall.Total.Should().Be(summary.Rows.Sum(row => row.Total));
    }

    [Fact]
    public async Task SeriesAsync_IgnoresOutboundKinds()
    {
        var minute = new DateTimeOffset(2026, 8, 20, 12, 7, 0, TimeSpan.Zero);
        var instance = $"pod-{Guid.NewGuid():N}";
        await using (var context = _fixture.CreateContext())
        {
            context.TrafficBuckets.AddRange(
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Http, Name = $"GET /api/orders-{Guid.NewGuid():N}", InstanceId = instance, Succeeded = 2 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Outbound, Name = $"Sim-{Guid.NewGuid():N}", InstanceId = instance, Faulted = 9 });
            await context.SaveChangesAsync();
        }

        await using var reading = _fixture.CreateContext();
        var series = await new TrafficReader(reading).SeriesAsync(minute.AddMinutes(-1), CancellationToken.None);

        series.Points.Should().ContainSingle(point => point.BucketStart == minute).Which.Total.Should().Be(2);
    }

    [Fact]
    public async Task OutboundAsync_SumsCallsAndAttemptsPerSystem()
    {
        var minute = new DateTimeOffset(2026, 8, 25, 12, 9, 0, TimeSpan.Zero);
        var system = $"sys-{Guid.NewGuid():N}"[..20];
        var instance = $"pod-{Guid.NewGuid():N}"[..20];
        await using (var context = _fixture.CreateContext())
        {
            context.TrafficBuckets.AddRange(
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Outbound, Name = system, InstanceId = instance, Succeeded = 8, Failed = 1, Faulted = 1 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.OutboundAttempt, Name = system, InstanceId = instance, Succeeded = 8, Failed = 1, Faulted = 4 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Http, Name = $"GET /{system}", InstanceId = instance, Succeeded = 50 });
            await context.SaveChangesAsync();
        }

        await using var reading = _fixture.CreateContext();
        var rows = await new TrafficReader(reading).OutboundAsync(minute.AddMinutes(-1), CancellationToken.None);

        var row = rows.Should().ContainSingle(r => r.System == system).Subject;
        row.Calls.Should().Be(10);
        row.Failed.Should().Be(1);
        row.Faulted.Should().Be(1);
        row.Attempts.Should().Be(13);
        rows.Should().NotContain(r => r.System == $"GET /{system}");
    }
}
