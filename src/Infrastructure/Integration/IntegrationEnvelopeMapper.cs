using RabbitMQ.Client;
using Wolverine;
using Wolverine.RabbitMQ.Internal;

namespace AiFramework.Infrastructure.Integration;

/// <summary>
/// Writes the standard AMQP properties an external consumer reads - message_id, type, content_type,
/// correlation_id - instead of Wolverine's own headers. Plain JSON on the wire, per ADR 0026.
/// </summary>
/// <remarks>
/// Reads the envelope's HEADERS, never <c>envelope.Message</c>. An outgoing envelope that another
/// host recovers from Postgres - its publisher stopped during a broker outage - reaches this mapper
/// with <c>Message == null</c>, because the body goes out from the stored bytes; its headers survive
/// that round trip. <see cref="WolverineIntegrationEventPublisher"/> sets both headers at publish
/// time. Plan, Verified API V4d; IntegrationEnvelopeMapperTests pins it.
/// </remarks>
public sealed class IntegrationEnvelopeMapper : IRabbitMqEnvelopeMapper
{
    /// <summary>The integration event's EventId; becomes the AMQP <c>message_id</c>.</summary>
    public const string EventIdHeader = "event-id";

    /// <summary>The contract's versioned name, e.g. <c>order.placed.v1</c>; becomes the AMQP <c>type</c>.</summary>
    public const string EventTypeHeader = "event-type";

    public static IntegrationEnvelopeMapper Instance { get; } = new();

    public void MapEnvelopeToOutgoing(Envelope envelope, IBasicProperties outgoing)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(outgoing);

        // EventId, not the envelope id: the envelope id changes if the domain event is redelivered
        // and published again; EventId does not, and it is what consumers dedupe on.
        if (envelope.TryGetHeader(EventIdHeader, out var eventId))
        {
            outgoing.MessageId = eventId;
        }

        if (envelope.TryGetHeader(EventTypeHeader, out var eventType))
        {
            outgoing.Type = eventType;
        }

        outgoing.ContentType = "application/json";
        outgoing.Persistent = true;
        outgoing.CorrelationId = envelope.CorrelationId;
    }

    // Outbound only - nothing listens through this mapper.
    public void MapIncomingToEnvelope(Envelope envelope, IReadOnlyBasicProperties incoming)
    {
    }
}
