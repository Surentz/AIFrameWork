using AiFramework.Application.Abstractions;
using AiFramework.Application.Statistics;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Statistics;

public sealed class GetPopulationAreasHandlerTests
{
    private readonly IPopulationStatistics _statistics = Substitute.For<IPopulationStatistics>();

    [Fact]
    public async Task HandleAsync_ReturnsTheSourcesAreasInItsOrder()
    {
        IReadOnlyList<StatisticsArea> areas = [new("000", "All Denmark"), new("101", "Copenhagen")];
        _statistics.GetAreasAsync(Arg.Any<CancellationToken>()).Returns(Result.Success(areas));

        var result = await new GetPopulationAreasHandler(_statistics).HandleAsync(new GetPopulationAreas(), CancellationToken.None);

        result.Value.Areas.Should().Equal(new PopulationAreaView("000", "All Denmark"), new PopulationAreaView("101", "Copenhagen"));
        result.Value.Source.Should().Be(GetPopulationHandler.Source);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSourceFails_PassesItsErrorThrough()
    {
        var unavailable = new Error(ErrorKind.Unavailable, "external_system.unavailable", "StatisticsDenmark is unavailable.");
        _statistics.GetAreasAsync(Arg.Any<CancellationToken>()).Returns(Result.Failure<IReadOnlyList<StatisticsArea>>(unavailable));

        var result = await new GetPopulationAreasHandler(_statistics).HandleAsync(new GetPopulationAreas(), CancellationToken.None);

        result.Error.Should().Be(unavailable);
    }
}
