using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;

namespace AiFramework.Infrastructure.EventPath;

/// <summary>
/// Every name this application gives an object on the broker, and the one place they are declared.
/// See ADR 0026 and the messaging skill.
/// </summary>
/// <remarks>
/// Names are dotted, RabbitMQ's convention - unlike the Postgres queue transport, which rewrote a
/// hyphen into an underscore, RabbitMQ keeps a name exactly as given.
/// </remarks>
public static class RabbitMqTopology
{
    /// <summary>Every event we publish. Topic; routing key = the domain event's registered name.</summary>
    public const string EventsExchange = "aiframework.events";

    /// <summary>
    /// The alternate exchange of <see cref="EventsExchange"/>. RabbitMQ DROPS a message published to
    /// an exchange with no matching binding; with no consumer yet, that would be every event. This
    /// catches them instead.
    /// </summary>
    public const string UnroutedExchange = "aiframework.events.unrouted";

    /// <summary>Where unrouted events wait. Capped so nobody reading it cannot fill a disk.</summary>
    public const string UnroutedQueue = "aiframework.events.unrouted";

    public const int UnroutedMaxLength = 100_000;

    /// <summary>
    /// Inbound shipment confirmations. Producers publish to it by name (default exchange). Declared
    /// by its listener with <see cref="SingleActiveConsumerArgument"/>: consumed one at a time.
    /// </summary>
    public const string ShipmentsQueue = "aiframework.shipments";

    /// <summary>The queue argument that makes the broker deliver to one consumer at a time.</summary>
    public const string SingleActiveConsumerArgument = "x-single-active-consumer";

    /// <summary>Declares the exchanges and the unrouted queue. Job and shipment queues are
    /// declared by their own routes and listeners.</summary>
    public static void Declare(RabbitMqTransportExpression rabbit)
    {
        ArgumentNullException.ThrowIfNull(rabbit);

        rabbit.DeclareExchange(UnroutedExchange, exchange =>
        {
            exchange.ExchangeType = ExchangeType.Fanout;
            exchange.BindQueue(UnroutedQueue);
        });

        rabbit.DeclareQueue(UnroutedQueue, queue =>
        {
            queue.QueueType = QueueType.quorum;
            queue.Arguments["x-max-length"] = UnroutedMaxLength;
            // Oldest first: when nobody reads it, the most recent events are the useful ones.
            queue.Arguments["x-overflow"] = "drop-head";
        });

        rabbit.DeclareExchange(EventsExchange, exchange =>
        {
            exchange.ExchangeType = ExchangeType.Topic;
            exchange.Arguments["alternate-exchange"] = UnroutedExchange;
        });
    }
}
