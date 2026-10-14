using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Application.Statistics;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class ParameterResolverTests
{
    private readonly IQueryDispatcher _queries = Substitute.For<IQueryDispatcher>();

    private ParameterResolver Resolver => new(_queries);

    private void AreasAre(params (string Code, string Name)[] areas) =>
        _queries.SendAsync(Arg.Any<GetPopulationAreas>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PopulationAreasView([.. areas.Select(a => new PopulationAreaView(a.Code, a.Name))], "Statistics Denmark")));

    [Fact]
    public void OrderId_ReadsAFullGuid()
    {
        var id = Guid.NewGuid();

        ParameterResolver.OrderId($"has order {id} shipped?").Should().Be(id);
    }

    [Fact]
    public void OrderId_IsNull_WithoutOne()
    {
        ParameterResolver.OrderId("has my last order shipped?").Should().BeNull();
    }

    [Fact]
    public void OrderPrefix_ReadsEightHexDigits()
    {
        ParameterResolver.OrderPrefix("status of order 3F2A1B9C please").Should().Be("3f2a1b9c");
    }

    [Fact]
    public void OrderPrefix_IsNull_WhenAFullGuidIsPresent()
    {
        ParameterResolver.OrderPrefix($"order {Guid.NewGuid()}").Should().BeNull();
    }

    [Theory]
    [InlineData("did anything fail in the last hour?", 1)]
    [InlineData("any failed jobs today?", 24)]
    [InlineData("failures in the last 6 hours", 6)]
    [InlineData("failures in the last 3 days", 72)]
    [InlineData("failures this week", 168)]
    [InlineData("failures in the last 900 hours", 168)]
    [InlineData("any failures?", 12)]
    public void Window_ReadsTheTimeSpan_OrFallsBack(string question, int hours)
    {
        ParameterResolver.Window(question, TimeSpan.FromHours(12)).Should().Be(TimeSpan.FromHours(hours));
    }

    [Fact]
    public async Task AreaAsync_TakesAThreeDigitCodeAsIs()
    {
        var result = await Resolver.AreaAsync("population of area 101", CancellationToken.None);

        result.Value.Area!.Code.Should().Be("101");
        await _queries.DidNotReceiveWithAnyArgs().SendAsync<PopulationAreasView>(default!, default);
    }

    [Fact]
    public async Task AreaAsync_MatchesAnAreaName()
    {
        AreasAre(("000", "All Denmark"), ("084", "Region Hovedstaden"), ("101", "Copenhagen"));

        var result = await Resolver.AreaAsync("How many people live in Copenhagen?", CancellationToken.None);

        result.Value.Area.Should().Be(new PopulationAreaView("101", "Copenhagen"));
    }

    [Fact]
    public async Task AreaAsync_ReadsDenmarkAsTheWholeCountry()
    {
        AreasAre(("000", "All Denmark"), ("084", "Region Hovedstaden"), ("101", "Copenhagen"));

        var result = await Resolver.AreaAsync("What is the population of Denmark?", CancellationToken.None);

        result.Value.Area!.Code.Should().Be("000");
    }

    [Fact]
    public async Task AreaAsync_OffersCandidates_WhenTwoNamesAreTooClose()
    {
        // "lyngbi" scores 0.556 against Lyngby and 0.500 against Lyngbye: both match, 0.056 apart,
        // under the 0.1 lead an answer needs.
        AreasAre(("173", "Lyngby"), ("174", "Lyngbye"));

        var result = await Resolver.AreaAsync("population of Lyngbi", CancellationToken.None);

        result.Value.Area.Should().BeNull();
        result.Value.Candidates.Select(c => c.Code).Should().BeEquivalentTo("173", "174");
    }

    [Fact]
    public async Task AreaAsync_FindsNothing_ForAnUnknownPlace()
    {
        AreasAre(("101", "Copenhagen"));

        var result = await Resolver.AreaAsync("population of Atlantis", CancellationToken.None);

        result.Value.Area.Should().BeNull();
        result.Value.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task AreaAsync_PassesOnAFailureToReadTheAreas()
    {
        _queries.SendAsync(Arg.Any<GetPopulationAreas>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PopulationAreasView>(new Error(ErrorKind.Unavailable, "statistics.unavailable", "Down.")));

        var result = await Resolver.AreaAsync("population of Copenhagen", CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }
}
