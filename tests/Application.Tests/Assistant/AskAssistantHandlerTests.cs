using System.Text.Json;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using AiFramework.Application.Monitoring;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AiFramework.Application.Tests.Assistant;

public sealed class AskAssistantHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Caller = Guid.NewGuid();
    private static readonly AssistantUsage Usage = new(10, 5, 0, 0);

    private readonly IAssistantModel _model = Substitute.For<IAssistantModel>();
    private readonly IAssistantConversations _conversations = Substitute.For<IAssistantConversations>();
    private readonly IAssistantOutput _output = Substitute.For<IAssistantOutput>();
    private readonly IQueryDispatcher _queries = Substitute.For<IQueryDispatcher>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly List<AssistantRequest> _requests = [];

    public AskAssistantHandlerTests()
    {
        _user.Id.Returns(Caller);
        _clock.UtcNow.Returns(Now);
        _queries.SendAsync(Arg.Any<GetJobHealth>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new JobHealthView(0, 1, 0, 0, Now)));
    }

    private AskAssistantHandler Handler => new(_model, _conversations, _output, new AssistantToolbox(_queries), _user, _clock);

    private static Result<AssistantModelTurn> Answer(string blocks = "answer") =>
        Result.Success(new AssistantModelTurn(new ModelTurn(blocks), [], AssistantStopReason.EndTurn, Usage));

    private static Result<AssistantModelTurn> ToolUse(string blocks = "tool") =>
        Result.Success(new AssistantModelTurn(
            new ModelTurn(blocks),
            [new AssistantToolCall("toolu_1", "job_health", JsonDocument.Parse("""{"window_hours":24}""").RootElement.Clone())],
            AssistantStopReason.ToolUse,
            Usage));

    private void ModelReturns(params Result<AssistantModelTurn>[] turns) =>
        _model.NextTurnAsync(Arg.Do<AssistantRequest>(_requests.Add), _output, Arg.Any<CancellationToken>())
            .Returns(turns[0], turns[1..]);

    private Task<Result<AssistantAnswer>> AskAsync(Guid? conversationId = null, string question = "Why did jobs fail?") =>
        Handler.HandleAsync(new AskAssistant(conversationId, question), CancellationToken.None);

    [Fact]
    public async Task HandleAsync_NewConversation_StartsItAndAppendsTheQuestionAndAnswer()
    {
        ModelReturns(Answer());

        var result = await AskAsync(question: "  Why?  ");

        result.Value.Outcome.Should().Be(AssistantOutcome.Answered);
        _conversations.Received(1).Start(result.Value.ConversationId, Caller, Now);
        _conversations.Received(1).Append(
            result.Value.ConversationId,
            Arg.Is<IReadOnlyList<AssistantTurn>>(t => t.Count == 2
                && t[0] == new UserQuestionTurn("Why?", Now)
                && t[1] == new ModelTurn("answer")),
            Now);
    }

    [Fact]
    public async Task HandleAsync_ToolUse_RunsTheToolReportsItAndSendsItsResultBack()
    {
        ModelReturns(ToolUse(), Answer());

        var result = await AskAsync();

        result.IsSuccess.Should().BeTrue();
        await _output.Received(1).ToolAsync("job_health", Arg.Any<CancellationToken>());
        _requests.Should().HaveCount(2);
        var results = _requests[1].Transcript[^1].Should().BeOfType<ToolResultsTurn>().Subject;
        results.Results.Should().ContainSingle(r => r.ToolCallId == "toolu_1" && !r.IsError);
        _conversations.Received(1).Append(Arg.Any<Guid>(), Arg.Is<IReadOnlyList<AssistantTurn>>(t => t.Count == 4), Now);
    }

    [Fact]
    public async Task HandleAsync_ModelKeepsCallingTools_EndsWithOneToolFreeTurn()
    {
        var turns = Enumerable.Repeat(ToolUse(), AskAssistantHandler.MaxToolRounds).Append(Answer()).ToArray();
        ModelReturns(turns);

        var result = await AskAsync();

        result.Value.Outcome.Should().Be(AssistantOutcome.Capped);
        _requests.Should().HaveCount(AskAssistantHandler.MaxToolRounds + 1);
        _requests.Take(AskAssistantHandler.MaxToolRounds).Should().OnlyContain(r => r.AllowTools);
        _requests[^1].AllowTools.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ExistingConversation_SendsItsHistoryFirst()
    {
        var id = Guid.NewGuid();
        AssistantTurn[] history = [new UserQuestionTurn("Earlier?", Now.AddMinutes(-5)), new ModelTurn("earlier answer")];
        _conversations.FindAsync(id, Arg.Any<CancellationToken>()).Returns(new AssistantConversation(id, Caller, 1, history));
        ModelReturns(Answer());

        await AskAsync(id);

        _requests[0].Transcript.Should().HaveCount(3);
        _requests[0].Transcript.Take(2).Should().Equal(history);
        _conversations.DidNotReceiveWithAnyArgs().Start(Guid.Empty, Guid.Empty, default);
    }

    [Fact]
    public async Task HandleAsync_SomeoneElsesConversation_IsNotFoundAndNeverCallsTheModel()
    {
        var id = Guid.NewGuid();
        _conversations.FindAsync(id, Arg.Any<CancellationToken>()).Returns(new AssistantConversation(id, Guid.NewGuid(), 1, []));

        var result = await AskAsync(id);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
        result.Error.Code.Should().Be("assistant.conversation_not_found");
        await _model.DidNotReceiveWithAnyArgs().NextTurnAsync(default!, default!, default);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_IsNotFound()
    {
        var result = await AskAsync(Guid.NewGuid());

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task HandleAsync_FullConversation_IsAValidationFailure()
    {
        var id = Guid.NewGuid();
        _conversations.FindAsync(id, Arg.Any<CancellationToken>())
            .Returns(new AssistantConversation(id, Caller, AskAssistantHandler.MaxQuestionsPerConversation, []));

        var result = await AskAsync(id);

        result.Error.Code.Should().Be("assistant.conversation_full");
    }

    [Fact]
    public async Task HandleAsync_NoCaller_IsUnauthorized()
    {
        _user.Id.Returns((Guid?)null);

        var result = await AskAsync();

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }

    [Fact]
    public async Task HandleAsync_ModelFails_FailsAndAppendsNothing()
    {
        ModelReturns(ToolUse(), Result.Failure<AssistantModelTurn>(new Error(ErrorKind.Unavailable, "assistant.unavailable", "Down.")));

        var result = await AskAsync();

        result.Error.Code.Should().Be("assistant.unavailable");
        _conversations.DidNotReceiveWithAnyArgs().Append(Guid.Empty, default!, default);
    }

    [Fact]
    public async Task HandleAsync_Refusal_FailsAndAppendsNothing()
    {
        ModelReturns(Result.Success(new AssistantModelTurn(new ModelTurn("r"), [], AssistantStopReason.Refusal, Usage)));

        var result = await AskAsync();

        result.Error.Code.Should().Be("assistant.refused");
        _conversations.DidNotReceiveWithAnyArgs().Append(Guid.Empty, default!, default);
    }

    [Fact]
    public async Task HandleAsync_ToolCallsWithoutAToolUseStop_FailsRatherThanStoringADanglingCall()
    {
        var truncated = ToolUse().Value with { StopReason = AssistantStopReason.MaxTokens };
        ModelReturns(Result.Success(truncated));

        var result = await AskAsync();

        result.Error.Code.Should().Be("assistant.incomplete");
        _conversations.DidNotReceiveWithAnyArgs().Append(Guid.Empty, default!, default);
    }

    [Fact]
    public async Task HandleAsync_MaxTokensWithoutToolCalls_IsATruncatedAnswer()
    {
        ModelReturns(Result.Success(new AssistantModelTurn(new ModelTurn("partial"), [], AssistantStopReason.MaxTokens, Usage)));

        var result = await AskAsync();

        result.Value.Outcome.Should().Be(AssistantOutcome.Truncated);
    }

    [Fact]
    public async Task HandleAsync_Cancelled_ThrowsAndAppendsNothing()
    {
        _model.NextTurnAsync(Arg.Any<AssistantRequest>(), _output, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = () => AskAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
        _conversations.DidNotReceiveWithAnyArgs().Append(Guid.Empty, default!, default);
    }
}
