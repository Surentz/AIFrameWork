using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant;

public sealed class AssistantHistoryHandlerTests
{
    private static readonly Guid Caller = Guid.NewGuid();
    private readonly IAssistantHistory _history = Substitute.For<IAssistantHistory>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public AssistantHistoryHandlerTests() => _user.Id.Returns(Caller);

    [Fact]
    public async Task List_ReturnsTheCallersConversations()
    {
        var summary = new AssistantConversationSummary(Guid.NewGuid(), "Why?", DateTimeOffset.UnixEpoch);
        _history.ListAsync(Caller, Arg.Any<CancellationToken>()).Returns([summary]);

        var result = await new GetAssistantConversationsHandler(_history, _user)
            .HandleAsync(new GetAssistantConversations(), CancellationToken.None);

        result.Value.Should().Equal(summary);
    }

    [Fact]
    public async Task Get_NotTheCallers_IsNotFound()
    {
        var id = Guid.NewGuid();
        _history.ReadAsync(id, Caller, Arg.Any<CancellationToken>()).Returns((AssistantConversationView?)null);

        var result = await new GetAssistantConversationHandler(_history, _user)
            .HandleAsync(new GetAssistantConversation(id), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
        result.Error.Code.Should().Be("assistant.conversation_not_found");
    }

    [Fact]
    public async Task Rename_TrimsTheTitleAndSucceeds()
    {
        var id = Guid.NewGuid();
        _history.RenameAsync(id, Caller, "Orders", Arg.Any<CancellationToken>()).Returns(true);

        var result = await new RenameAssistantConversationHandler(_history, _user)
            .HandleAsync(new RenameAssistantConversation(id, "  Orders  "), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_NotFound_IsNotFound()
    {
        _history.DeleteAsync(Arg.Any<Guid>(), Caller, Arg.Any<CancellationToken>()).Returns(false);

        var result = await new DeleteAssistantConversationHandler(_history, _user)
            .HandleAsync(new DeleteAssistantConversation(Guid.NewGuid()), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Any_WithoutACaller_IsUnauthorizedAndTouchesNothing()
    {
        _user.Id.Returns((Guid?)null);

        var result = await new GetAssistantConversationsHandler(_history, _user)
            .HandleAsync(new GetAssistantConversations(), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
        await _history.DidNotReceiveWithAnyArgs().ListAsync(Guid.Empty, CancellationToken.None);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RenameValidator_BlankTitle_IsInvalid(string title)
    {
        new RenameAssistantConversationValidator().Validate(new RenameAssistantConversation(Guid.NewGuid(), title)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void RenameValidator_TitleOverTheLimit_IsInvalid()
    {
        new RenameAssistantConversationValidator()
            .Validate(new RenameAssistantConversation(Guid.NewGuid(), new string('x', AssistantTitle.MaxLength + 1)))
            .IsValid.Should().BeFalse();
    }
}
