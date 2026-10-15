using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant;

public sealed class RateAssistantAnswerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Caller = Guid.NewGuid();
    private readonly IAssistantRatings _ratings = Substitute.For<IAssistantRatings>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public RateAssistantAnswerHandlerTests()
    {
        _user.Id.Returns(Caller);
        _clock.UtcNow.Returns(Now);
    }

    private Task<Result<bool>> RateAsync(RateResult outcome, AnswerRating? rating = AnswerRating.Up)
    {
        var id = Guid.NewGuid();
        _ratings.RateAsync(id, Caller, 3, rating, Now, Arg.Any<CancellationToken>()).Returns(outcome);
        return new RateAssistantAnswerHandler(_ratings, _user, _clock)
            .HandleAsync(new RateAssistantAnswer(id, 3, rating), CancellationToken.None);
    }

    [Fact]
    public async Task HandleAsync_Rated_Succeeds()
    {
        (await RateAsync(RateResult.Rated)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_NullClears()
    {
        (await RateAsync(RateResult.Rated, rating: null)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_NotFound_IsTheSameNotFoundAsAMissingConversation()
    {
        var result = await RateAsync(RateResult.NotFound);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
        result.Error.Code.Should().Be("assistant.conversation_not_found");
    }

    [Fact]
    public async Task HandleAsync_NotAnAnswer_IsAValidationFailure()
    {
        var result = await RateAsync(RateResult.NotAnAnswer);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("assistant.not_an_answer");
    }

    [Fact]
    public async Task HandleAsync_WithoutACaller_IsUnauthorized()
    {
        _user.Id.Returns((Guid?)null);

        var result = await new RateAssistantAnswerHandler(_ratings, _user, _clock)
            .HandleAsync(new RateAssistantAnswer(Guid.NewGuid(), 3, AnswerRating.Up), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }

    [Theory]
    [InlineData(-1)]
    public void Validator_ANegativeTurn_IsInvalid(int turn)
    {
        new RateAssistantAnswerValidator().Validate(new RateAssistantAnswer(Guid.NewGuid(), turn, AnswerRating.Up)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_AnUndefinedRating_IsInvalid()
    {
        new RateAssistantAnswerValidator().Validate(new RateAssistantAnswer(Guid.NewGuid(), 1, (AnswerRating)7)).IsValid.Should().BeFalse();
    }
}
