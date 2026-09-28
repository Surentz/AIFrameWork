using System.Text.Json;
using AiFramework.Application.IntegrationEvents;
using AiFramework.Infrastructure.EventPath;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.Runtime.Serialization;

namespace AiFramework.Infrastructure.Integration;

/// <summary>One outbound contract's registration, captured over the closed generic (reflection-free, like JobDescriptor).</summary>
public sealed record IntegrationEventDescriptor(
    Type EventType, string TypeName, string RoutingKey, Action<WolverineOptions> Route)
{
    public static IntegrationEventDescriptor For<TEvent>()
        where TEvent : IIntegrationEvent =>
        new(typeof(TEvent), TEvent.TypeName, TEvent.RoutingKey, static opts =>
            opts.PublishMessage<TEvent>()
                .ToRabbitRoutingKey(RabbitMqTopology.EventsExchange, TEvent.RoutingKey)
                .UseInterop(IntegrationEnvelopeMapper.Instance)
                // A private copy per route, never IntegrationJson.Options itself: Wolverine's
                // serializer adds a converter to the options it is given, which throws on the
                // read-only shared instance (plan, Verified API V2b).
                .DefaultSerializer(new SystemTextJsonSerializer(new JsonSerializerOptions(IntegrationJson.Options))));
}

/// <summary>
/// Every outbound contract, explicitly, and the one inbound listener. IntegrationEventRegistrationTests
/// enforces outbound completeness.
/// </summary>
public static class IntegrationEventRegistration
{
    public static IReadOnlyList<IntegrationEventDescriptor> Outbound { get; } =
    [
        IntegrationEventDescriptor.For<OrderPlacedV1>(),
        IntegrationEventDescriptor.For<OrderShippedV1>(),
        IntegrationEventDescriptor.For<OrderCancelledV1>(),
        IntegrationEventDescriptor.For<ProductPriceChangedV1>(),
    ];

    /// <summary>Routes every outbound contract. Runs on both hosts: both publish.</summary>
    public static void MapOutbound(WolverineOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        foreach (var descriptor in Outbound)
        {
            descriptor.Route(opts);
        }
    }

    /// <summary>The inbound listener. Worker only - never called for the API.</summary>
    /// <remarks>
    /// <para>
    /// <b>One message at a time, across every worker replica.</b> RecordShipment is idempotent by
    /// the order's state, which it checks in memory, and Order has no concurrency token. Two
    /// confirmations for one order handled at once both see it Placed and both ship it: two
    /// OrderShipped rows, a second buyer notification, and a second order.shipped.v1 under a
    /// different eventId that no consumer can dedupe (ShipmentInboundTests found three rows for
    /// three confirmations). Serial consumption closes that without a schema change:
    /// </para>
    /// <list type="bullet">
    /// <item><c>x-single-active-consumer</c>: the broker delivers to one consumer at a time, so
    /// competing worker replicas take turns rather than splitting the queue. The others stand by
    /// and take over when it disconnects.</item>
    /// <item><c>Sequential()</c>: that one consumer's process handles one message at a time. The
    /// listener is durable (UseDurableInboxOnAllListeners), which is the mode this applies to.</item>
    /// </list>
    /// <para>
    /// RabbitMQ refuses to redeclare an existing queue with different arguments
    /// (PRECONDITION_FAILED), so a broker that already holds this queue without the argument stops
    /// the worker at startup until the queue is deleted once - the messaging skill's upgrade section.
    /// </para>
    /// </remarks>
    public static void ListenForShipments(WolverineOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        opts.ListenToRabbitQueue(RabbitMqTopology.ShipmentsQueue)
            .ConfigureQueue(static queue =>
                queue.Arguments[RabbitMqTopology.SingleActiveConsumerArgument] = true)
            .Sequential()
            // Plain JSON from a producer with no Wolverine: no type header to route on, so every
            // message on this queue is this type.
            .DefaultIncomingMessage<ShipmentConfirmedV1>()
            // A private copy, for the reason the outbound routes above give (V2b).
            .DefaultSerializer(new SystemTextJsonSerializer(new JsonSerializerOptions(IntegrationJson.Options)));
    }
}
