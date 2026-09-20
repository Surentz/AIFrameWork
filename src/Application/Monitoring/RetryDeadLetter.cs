using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>
/// Puts one dead-lettered message back in play.
/// </summary>
/// <remarks>
/// <b>This is a write from the API into the worker's world, and it works because the transport is
/// PostgreSQL.</b> Marking the stored envelope replayable is all the API does; the worker picks it
/// up from the shared store on its own, and the two never talk. That property is what makes the
/// monitoring page possible without exposing the worker through the ingress — and it is the first
/// thing to re-examine if the transport ever becomes RabbitMQ. See ADR 0021 and ADR 0016.
/// </remarks>
public sealed record RetryDeadLetter(Guid MessageId) : ICommand<bool>;

public sealed class RetryDeadLetterHandler(IDeadLetterStore deadLetters)
    : ICommandHandler<RetryDeadLetter, bool>
{
    public async Task<Result<bool>> HandleAsync(
        RetryDeadLetter command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var replayed = await deadLetters.ReplayAsync(command.MessageId, cancellationToken)
            .ConfigureAwait(false);

        // NotFound rather than a silent true: an operator who clicked retry on a message another
        // operator had already discarded should be told, not left watching for a run that will
        // never appear.
        return replayed
            ? Result.Success(true)
            : Result.Failure<bool>(new Error(
                ErrorKind.NotFound,
                "dead_letter.not_found",
                $"No dead-lettered message with id '{command.MessageId}'."));
    }
}
