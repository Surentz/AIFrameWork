using AiFramework.Infrastructure.Integration;
using FluentAssertions;
using RabbitMQ.Client;
using Wolverine;

namespace AiFramework.Infrastructure.Tests.Integration;

/// <summary>
/// Pins the mapper to the envelope's headers. An outgoing envelope recovered from Postgres by
/// another host reaches the mapper with <c>Message == null</c> - the body goes out from the stored
/// bytes - while its headers survive the round trip (plan, Verified API V4d). A mapper that read
/// the message would send that event with no <c>type</c> and no <c>message_id</c>.
/// </summary>
public sealed class IntegrationEnvelopeMapperTests
{
    [Fact]
    public void MapEnvelopeToOutgoing_WithNoMessageButTheHeadersSet_WritesMessageIdAndTypeFromTheHeaders()
    {
        var eventId = Guid.NewGuid().ToString();
        var envelope = new Envelope { CorrelationId = "trace-1" };
        envelope.Headers[IntegrationEnvelopeMapper.EventIdHeader] = eventId;
        envelope.Headers[IntegrationEnvelopeMapper.EventTypeHeader] = "order.placed.v1";
        var outgoing = new BasicProperties();

        IntegrationEnvelopeMapper.Instance.MapEnvelopeToOutgoing(envelope, outgoing);

        envelope.Message.Should().BeNull("this is the recovered-envelope case the mapper must survive");
        outgoing.MessageId.Should().Be(eventId);
        outgoing.Type.Should().Be("order.placed.v1");
        outgoing.ContentType.Should().Be("application/json");
        outgoing.Persistent.Should().BeTrue();
        outgoing.CorrelationId.Should().Be("trace-1");
    }

    [Fact]
    public void MapEnvelopeToOutgoing_WithoutTheHeaders_LeavesMessageIdAndTypeUnset()
    {
        var outgoing = new BasicProperties();

        IntegrationEnvelopeMapper.Instance.MapEnvelopeToOutgoing(new Envelope(), outgoing);

        outgoing.IsMessageIdPresent().Should().BeFalse();
        outgoing.IsTypePresent().Should().BeFalse();
    }
}
