using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant.Quick;
using FluentValidation;

namespace AiFramework.Application.Assistant;

public sealed record AssistantConversationSummary(Guid Id, string Title, DateTimeOffset LastActiveAt);

/// <summary>
/// One message as the page shows it: a question, or everything the model wrote for it with the
/// tools it used, or a quick reply with its card or suggestions. Built from the stored turns for
/// display only; it never goes back to the model.
/// </summary>
public sealed record AssistantDisplayMessage(
    string Role,
    string Text,
    IReadOnlyList<string> Tools,
    QuickAnswer? Answer = null,
    QuickSuggestions? Suggestions = null)
{
    public const string UserRole = "user";

    public const string AssistantRole = "assistant";
}

public sealed record AssistantConversationView(Guid Id, string Title, IReadOnlyList<AssistantDisplayMessage> Messages);

/// <summary>
/// The sidebar's view of the transcripts, always for one owner: another owner's conversation is
/// indistinguishable from a missing one. Rename and delete are tracked and committed by the unit of
/// work; rename never touches a stored turn (ADR 0033: transcripts are append-only).
/// </summary>
public interface IAssistantHistory
{
    /// <summary>The owner's conversations, most recently active first.</summary>
    public Task<IReadOnlyList<AssistantConversationSummary>> ListAsync(Guid ownerId, CancellationToken cancellationToken);

    public Task<AssistantConversationView?> ReadAsync(Guid id, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>False when the owner has no such conversation.</summary>
    public Task<bool> RenameAsync(Guid id, Guid ownerId, string title, CancellationToken cancellationToken);

    /// <summary>False when the owner has no such conversation. Its turns go with it.</summary>
    public Task<bool> DeleteAsync(Guid id, Guid ownerId, CancellationToken cancellationToken);
}

public sealed record GetAssistantConversations : IQuery<IReadOnlyList<AssistantConversationSummary>>;

public sealed record GetAssistantConversation(Guid Id) : IQuery<AssistantConversationView>;

public sealed record RenameAssistantConversation(Guid Id, string Title) : ICommand<bool>;

public sealed record DeleteAssistantConversation(Guid Id) : ICommand<bool>;

public sealed class RenameAssistantConversationValidator : AbstractValidator<RenameAssistantConversation>
{
    public RenameAssistantConversationValidator()
    {
        RuleFor(c => c.Title)
            .Must(t => t is not null && t.Trim().Length is >= 1 and <= AssistantTitle.MaxLength)
            .WithMessage($"A title is 1 to {AssistantTitle.MaxLength} characters.");
    }
}

internal static class AssistantHistoryErrors
{
    public static Error NoCaller { get; } = new(ErrorKind.Unauthorized, "assistant.no_caller", "Sign in to use the assistant.");

    public static Error NotFound { get; } = new(
        ErrorKind.NotFound, "assistant.conversation_not_found", "That conversation does not exist or has expired.");
}

public sealed class GetAssistantConversationsHandler(IAssistantHistory history, ICurrentUser currentUser)
    : IQueryHandler<GetAssistantConversations, IReadOnlyList<AssistantConversationSummary>>
{
    public async Task<Result<IReadOnlyList<AssistantConversationSummary>>> HandleAsync(
        GetAssistantConversations query, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } ownerId)
        {
            return Result.Failure<IReadOnlyList<AssistantConversationSummary>>(AssistantHistoryErrors.NoCaller);
        }

        return Result.Success(await history.ListAsync(ownerId, cancellationToken).ConfigureAwait(false));
    }
}

public sealed class GetAssistantConversationHandler(IAssistantHistory history, ICurrentUser currentUser)
    : IQueryHandler<GetAssistantConversation, AssistantConversationView>
{
    public async Task<Result<AssistantConversationView>> HandleAsync(
        GetAssistantConversation query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (currentUser.Id is not { } ownerId)
        {
            return Result.Failure<AssistantConversationView>(AssistantHistoryErrors.NoCaller);
        }

        var view = await history.ReadAsync(query.Id, ownerId, cancellationToken).ConfigureAwait(false);
        return view is null
            ? Result.Failure<AssistantConversationView>(AssistantHistoryErrors.NotFound)
            : Result.Success(view);
    }
}

public sealed class RenameAssistantConversationHandler(IAssistantHistory history, ICurrentUser currentUser)
    : ICommandHandler<RenameAssistantConversation, bool>
{
    public async Task<Result<bool>> HandleAsync(RenameAssistantConversation command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } ownerId)
        {
            return Result.Failure<bool>(AssistantHistoryErrors.NoCaller);
        }

        return await history.RenameAsync(command.Id, ownerId, command.Title.Trim(), cancellationToken).ConfigureAwait(false)
            ? Result.Success(true)
            : Result.Failure<bool>(AssistantHistoryErrors.NotFound);
    }
}

public sealed class DeleteAssistantConversationHandler(IAssistantHistory history, ICurrentUser currentUser)
    : ICommandHandler<DeleteAssistantConversation, bool>
{
    public async Task<Result<bool>> HandleAsync(DeleteAssistantConversation command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } ownerId)
        {
            return Result.Failure<bool>(AssistantHistoryErrors.NoCaller);
        }

        return await history.DeleteAsync(command.Id, ownerId, cancellationToken).ConfigureAwait(false)
            ? Result.Success(true)
            : Result.Failure<bool>(AssistantHistoryErrors.NotFound);
    }
}
