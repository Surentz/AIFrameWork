using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant;

public sealed class GetAssistantInsightsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    private readonly IAssistantInsightsReader _reader = Substitute.For<IAssistantInsightsReader>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public GetAssistantInsightsHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _reader.DaysAsync(default, default).ReturnsForAnyArgs([]);
        _reader.TargetsAsync(default, default, default).ReturnsForAnyArgs([]);
        _reader.MissesAsync(default, default, default).ReturnsForAnyArgs([]);
    }

    private Task<Result<AssistantInsightsView>> GetAsync(int days) =>
        new GetAssistantInsightsHandler(_reader, _clock).HandleAsync(new GetAssistantInsights(days), CancellationToken.None);

    [Fact]
    public async Task HandleAsync_ReadsFromTheStartOfTheWindow()
    {
        var result = await GetAsync(7);

        result.Value.Since.Should().Be(Now.AddDays(-7));
        await _reader.Received(1).MissesAsync(Now.AddDays(-7), GetAssistantInsightsHandler.MaxMisses, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ScrubsEveryMissedQuestion()
    {
        _reader.MissesAsync(default, default, default).ReturnsForAnyArgs(
            [new InsightsMiss(Now, AnswerPath.Ai, null, null, null, "Why can't jane@example.com sign in from 10.1.2.3?")]);

        var result = await GetAsync(7);

        var question = result.Value.Misses.Single().Question;
        question.Should().NotContain("jane@example.com").And.NotContain("10.1.2.3");
        question.Should().Contain("[email]");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task HandleAsync_AWindowOutsideOneTo30Days_IsInvalid(int days)
    {
        var result = await GetAsync(days);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("assistant.insights_window");
    }
}
