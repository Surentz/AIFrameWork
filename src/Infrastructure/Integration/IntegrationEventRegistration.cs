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
    public static void ListenForShipments(WolverineOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        opts.ListenToRabbitQueue(RabbitMqTopology.ShipmentsQueue)
            // Plain JSON from a producer with no Wolverine: no type header to route on, so every
            // message on this queue is this type.
            .DefaultIncomingMessage<ShipmentConfirmedV1>()
            // A private copy, for the reason the outbound routes above give (V2b).
            .DefaultSerializer(new SystemTextJsonSerializer(new JsonSerializerOptions(IntegrationJson.Options)));
    }
}
