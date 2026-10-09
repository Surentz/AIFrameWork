using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Monitoring;

public sealed class GetExternalSystemStatusHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly IExternalSystemStatusReader _statuses = Substitute.For<IExternalSystemStatusReader>();
    private readonly ITrafficReader _traffic = Substitute.For<ITrafficReader>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public GetExternalSystemStatusHandlerTests() => _clock.UtcNow.Returns(Now);

    private async Task<ExternalSystemsView> HandleAsync(
        IReadOnlyList<ExternalSystemStatusView> statuses, IReadOnlyList<OutboundTrafficView> traffic)
    {
        _statuses.ListAsync(Arg.Any<CancellationToken>()).Returns(statuses);
        _traffic.OutboundAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(traffic);
        var result = await new GetExternalSystemStatusHandler(_statuses, _traffic, _clock)
            .HandleAsync(new GetExternalSystemStatus(), CancellationToken.None);
        return result.Value;
    }

    private static ExternalSystemStatusView Status(string name, DateTimeOffset checkedAt) =>
        new(name, ExternalSystemState.Healthy, "reachable (401)", checkedAt, Now.AddDays(60), TokenOk: true);

    [Fact]
    public async Task HandleAsync_JoinsStatusWithTrafficByName()
    {
        var view = await HandleAsync(
            [Status("Partner", Now.AddSeconds(-30))],
            [new OutboundTrafficView("Partner", Calls: 10, Failed: 1, Faulted: 2, Attempts: 13, P95Ms: 250)]);

        var row = view.Systems.Should().ContainSingle().Subject;
        row.State.Should().Be(ExternalSystemState.Healthy);
        row.Calls.Should().Be(10);
        row.Attempts.Should().Be(13);
        row.P95Ms.Should().Be(250);
        row.Stale.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_WhenARowIsOlderThanThreeMinutes_MarksItStale()
    {
        var view = await HandleAsync([Status("Partner", Now - TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1))], []);

        view.Systems.Single().Stale.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_ForTrafficWithoutAStatusRow_ListsTheSystemWithNoState()
    {
        var view = await HandleAsync([], [new OutboundTrafficView("Partner", 3, 0, 0, 3, 40)]);

        var row = view.Systems.Should().ContainSingle().Subject;
        row.State.Should().BeNull();
        row.CheckedAt.Should().BeNull();
        row.Stale.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ForAStatusWithoutTraffic_ReportsZeroCalls()
    {
        var view = await HandleAsync([Status("Partner", Now)], []);

        view.Systems.Single().Calls.Should().Be(0);
        view.Systems.Single().P95Ms.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_ReadsTrafficForTheLastHour()
    {
        await HandleAsync([], []);

        await _traffic.Received(1).OutboundAsync(Now - TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
    }
}
