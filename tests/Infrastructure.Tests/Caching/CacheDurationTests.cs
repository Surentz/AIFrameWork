using AiFramework.Infrastructure.Caching;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Caching;

/// <summary>
/// The clamp is a pure function precisely so it can be tested without waiting. HybridCache
/// expires entries on its own internal clock, which this repository's IClock cannot reach, and
/// tests/CLAUDE.md forbids Thread.Sleep — so the arithmetic is tested here and the library's
/// honouring of Expiration is taken as its own contract.
/// </summary>
public sealed class CacheDurationTests
{
    [Fact]
    public void Clamp_WhenRequestedIsBelowTheMaximum_ReturnsTheRequested()
    {
        var clamped = CacheDuration.Clamp(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));

        clamped.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Clamp_WhenRequestedExceedsTheMaximum_ReturnsTheMaximum()
    {
        var clamped = CacheDuration.Clamp(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));

        clamped.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Clamp_WhenRequestedEqualsTheMaximum_ReturnsTheMaximum()
    {
        var clamped = CacheDuration.Clamp(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        clamped.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Clamp_WithAZeroRequest_ReturnsZeroRatherThanTheMaximum()
    {
        var clamped = CacheDuration.Clamp(TimeSpan.Zero, TimeSpan.FromMinutes(1));

        clamped.Should().Be(
            TimeSpan.Zero,
            "MaximumDuration is a ceiling, not a default — it must never raise a duration");
    }
}
