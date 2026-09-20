using AiFramework.Application.Monitoring;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// Wolverine's dead-letter queue, behind this application's port.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through Wolverine's own <see cref="IDeadLetters"/> API, never raw SQL against its tables.</b>
/// This feature's plan assumed no supported surface existed and called for reading
/// <c>wolverine.wolverine_dead_letters</c> directly; 6.33 has one, it covers both querying and
/// replaying, and using it means nothing here is coupled to a schema Wolverine owns and migrates
/// on its own timetable.
/// </para>
/// <para>
/// <see cref="IMessageStore"/> is resolved per call rather than injected, because a host running
/// with <c>Wolverine:Durable=false</c> has no durable store at all — that is how <c>codegen
/// write</c> and the contract build run. A monitoring page on such a host reports an empty queue
/// rather than failing to start.
/// </para>
/// </remarks>
internal sealed class DeadLetterStore(IServiceProvider services) : IDeadLetterStore
{
    public async Task<DeadLetterPage> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken)
    {
        if (Resolve() is not { } deadLetters)
        {
            return new DeadLetterPage([], 0, page);
        }

        var results = await deadLetters
            .QueryAsync(
                new DeadLetterEnvelopeQuery { PageNumber = page, PageSize = pageSize },
                cancellationToken)
            .ConfigureAwait(false);

        var items = results.Envelopes
            .Select(envelope => new DeadLetterView(
                envelope.Id,
                envelope.MessageType,
                envelope.ExceptionType,
                envelope.ExceptionMessage,
                envelope.SentAt,
                envelope.ReceivedAt,
                envelope.Replayable))
            .ToList();

        return new DeadLetterPage(items, results.TotalCount, page);
    }

    public async Task<bool> ReplayAsync(Guid messageId, CancellationToken cancellationToken)
    {
        if (Resolve() is not { } deadLetters)
        {
            return false;
        }

        // Checked before replaying rather than trusting ReplayAsync's silence: it takes a query
        // and reports nothing about how many rows it matched, so an id that is not there would
        // otherwise answer "retried" and leave an operator waiting for a run that never comes.
        var existing = await deadLetters
            .DeadLetterEnvelopeByIdAsync(messageId)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return false;
        }

        await deadLetters
            .ReplayAsync(new DeadLetterEnvelopeQuery([messageId]), cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    private IDeadLetters? Resolve() =>
        (services.GetService(typeof(IMessageStore)) as IMessageStore)?.DeadLetters;
}
