using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Application.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class AskQuickHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Caller = Guid.NewGuid();
    private static readonly QuickRouting Routing = new(0.8, 0.05, 0.5);

    private readonly IAssistantConversations _conversations = Substitute.For<IAssistantConversations>();
    private readonly IAssistantOutput _output = Substitute.For<IAssistantOutput>();
    private readonly IQueryDispatcher _queries = Substitute.For<IQueryDispatcher>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly QuickIndexState _state = new();
    private readonly FakeEmbedder _embedder = new FakeEmbedder()
        .Add("Show my recent orders", 1, 0)
        .Add("Is my export ready?", 0, 1);

    public AskQuickHandlerTests()
    {
        _user.Id.Returns(Caller);
        _clock.UtcNow.Returns(Now);
        var catalog = new QuickCatalog(
            [new(QuickIntents.RecentOrders, "Show my recent orders"), new(QuickIntents.ExportStatus, "Is my export ready?")], []);
        var built = QuickIndexBuilder.Build(catalog, _embedder).Value;
        _state.Publish(built.Index, built.Spelling);
        _queries.SendAsync(Arg.Any<GetOrders>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new OrderPage([], null)));
    }

    private AskQuickHandler Handler => new(
        new IntentRouter(_state, _embedder, Routing),
        new QuickAnswerComposer(_queries, new ParameterResolver(_queries)),
        _conversations, _output, _user, _clock);

    private Task<Result<AssistantAnswer>> AskAsync(string question, Guid? conversation = null) =>
        Handler.HandleAsync(new AskQuick(conversation, question, ToolScope.Member), CancellationToken.None);

    // Questions here use only catalog words, so the spelling pass leaves them alone and the fake
    // embedder (which throws on an unknown text) sees exactly the text it was given.
    [Fact]
    public async Task AMatchedQuestion_IsAnsweredWrittenAndStored()
    {
        _embedder.Add("show my orders", 1, 0.05f);

        var result = await AskAsync("show my orders");

        result.IsSuccess.Should().BeTrue();
        await _output.Received(1).AnswerAsync(Arg.Is<QuickAnswer>(a => a.Text == "You haven't placed any orders yet."), Arg.Any<CancellationToken>());
        _conversations.Received(1).Start(result.Value.ConversationId, Caller, "show my orders", Now);
        _conversations.Received(1).Append(
            result.Value.ConversationId,
            Arg.Is<IReadOnlyList<AssistantTurn>>(turns => turns.Count == 2 && turns[0] is UserQuestionTurn && turns[1] is QuickAnswerTurn),
            Now);
    }

    [Fact]
    public async Task AnUnsureQuestion_GetsSuggestions_AndStoresThem()
    {
        _embedder.Add("orders or export", 1, 1);

        await AskAsync("orders or export");

        await _output.Received(1).SuggestionsAsync(
            Arg.Is<QuickSuggestions>(s => s.Options.Count == 2 && s.Text == "I'm not sure what you mean. Did you mean one of these?"),
            Arg.Any<CancellationToken>());
        _conversations.Received(1).Append(Arg.Any<Guid>(), Arg.Is<IReadOnlyList<AssistantTurn>>(t => t[1] is SuggestionsTurn), Now);
    }

    [Fact]
    public async Task WhenTheIndexIsNotReady_TheReplySaysSo_AndNothingIsGuessed()
    {
        var handler = new AskQuickHandler(
            new IntentRouter(new QuickIndexState(), _embedder, Routing),
            new QuickAnswerComposer(_queries, new ParameterResolver(_queries)),
            _conversations, _output, _user, _clock);

        await handler.HandleAsync(new AskQuick(null, "anything", ToolScope.Member), CancellationToken.None);

        await _output.Received(1).SuggestionsAsync(AskQuickHandler.NotAvailable, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFullConversation_IsRefused()
    {
        var id = Guid.NewGuid();
        _conversations.FindAsync(id, Arg.Any<CancellationToken>())
            .Returns(new AssistantConversation(id, Caller, AssistantLimits.MaxQuestionsPerConversation, []));

        var result = await AskAsync("Show my recent orders", id);

        result.Error.Code.Should().Be("assistant.conversation_full");
        await _output.DidNotReceiveWithAnyArgs().AnswerAsync(default!, default);
    }

    [Fact]
    public async Task AnotherUsersConversation_IsNotFound()
    {
        var id = Guid.NewGuid();
        _conversations.FindAsync(id, Arg.Any<CancellationToken>()).Returns(new AssistantConversation(id, Guid.NewGuid(), 1, []));

        var result = await AskAsync("Show my recent orders", id);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public void AiQuestions_CountsOnlyQuestionsTheModelAnswered()
    {
        IReadOnlyList<AssistantTurn> turns =
        [
            new UserQuestionTurn("a", Now), new ModelTurn("[]"),
            new UserQuestionTurn("b", Now), new QuickAnswerTurn(new QuickAnswer(QuickAnswerKind.Instant, null, "x", [], null, null, null), "x"),
            new UserQuestionTurn("c", Now), new ModelTurn("[]"), new ToolResultsTurn([]), new ModelTurn("[]"),
        ];

        AssistantLimits.AiQuestions(turns).Should().Be(2);
    }
}
