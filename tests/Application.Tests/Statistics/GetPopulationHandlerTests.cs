using AiFramework.Application.Abstractions;
using AiFramework.Application.Statistics;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Statistics;

public sealed class GetPopulationHandlerTests
{
    private readonly IPopulationStatistics _statistics = Substitute.For<IPopulationStatistics>();

    private Task<Result<PopulationView>> HandleAsync(string area) =>
        new GetPopulationHandler(_statistics).HandleAsync(new GetPopulation(area), CancellationToken.None);

    [Fact]
    public async Task HandleAsync_ForAPublishedArea_ReturnsTheFigureWithItsSource()
    {
        _statistics.GetLatestAsync("101", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AreaPopulation("101", "Copenhagen", "2026Q3", 670_389)));

        var result = await HandleAsync("101");

        result.Value.Should().Be(new PopulationView("101", "Copenhagen", "2026Q3", 670_389, GetPopulationHandler.Source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("1011")]
    [InlineData("1a1")]
    [InlineData("١٠١")]
    public async Task HandleAsync_ForAMalformedArea_IsAValidationErrorWithoutCallingTheSource(string area)
    {
        var result = await HandleAsync(area);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
        await _statistics.DidNotReceive().GetLatestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenTheSourceFails_PassesItsErrorThrough()
    {
        var unavailable = new Error(ErrorKind.Unavailable, "external_system.unavailable", "StatisticsDenmark is unavailable.");
        _statistics.GetLatestAsync("101", Arg.Any<CancellationToken>()).Returns(Result.Failure<AreaPopulation>(unavailable));

        var result = await HandleAsync("101");

        result.Error.Should().Be(unavailable);
    }
}
