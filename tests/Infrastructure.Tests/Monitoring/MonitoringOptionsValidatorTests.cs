using AiFramework.Infrastructure.Monitoring;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Monitoring;

public sealed class MonitoringOptionsValidatorTests
{
    private static bool Succeeds(TimeSpan period) =>
        new MonitoringOptionsValidator()
            .Validate(null, new MonitoringOptions { ExternalSystemStatusPeriod = period }).Succeeded;

    [Fact]
    public void Validate_TheDefaultPeriod_Succeeds() =>
        new MonitoringOptionsValidator().Validate(null, new MonitoringOptions()).Succeeded.Should().BeTrue();

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(60)]
    public void Validate_APeriodFromOneSecondToOneMinute_Succeeds(int seconds) =>
        Succeeds(TimeSpan.FromSeconds(seconds)).Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(61)]
    [InlineData(300)]
    public void Validate_APeriodOutsideOneSecondToOneMinute_Fails(int seconds) =>
        Succeeds(TimeSpan.FromSeconds(seconds)).Should().BeFalse();
}
