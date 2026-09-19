using System.Text.Json;
using AiFramework.Api.Notifications;
using AiFramework.Domain.Notifications;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiFramework.Api.IntegrationTests.Notifications;

/// <summary>
/// What the realtime push actually puts on the wire.
/// </summary>
/// <remarks>
/// SignalR serializes hub payloads with a THIRD, unrelated JsonSerializerOptions —
/// JsonHubProtocolOptions.PayloadSerializerOptions. Neither AddControllers().AddJsonOptions
/// (which controllers use) nor ConfigureHttpJsonOptions (which AddOpenApi's schema generator
/// reads) reaches it. Without AddJsonProtocol, a push therefore sends "kind":0 while
/// GET /api/notifications sends "kind":"OrderPlaced" — two contracts for one concept, and the
/// REST one is the only one in schema.d.ts.
///
/// Asserted against the real host's own resolved options rather than a hand-built
/// JsonSerializerOptions, because the thing under test IS the configuration.
/// </remarks>
[Collection(nameof(ApiFactoryCollection))]
public sealed class NotificationPushPayloadTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _realtime;

    public NotificationPushPayloadTests(ApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _realtime = factory.WithWebHostBuilder(
            builder => builder.UseSetting("Realtime:Enabled", "true"));
    }

    public void Dispose() => _realtime.Dispose();

    private JsonSerializerOptions PayloadOptions() =>
        _realtime.Services
            .GetRequiredService<IOptions<JsonHubProtocolOptions>>()
            .Value
            .PayloadSerializerOptions;

    [Fact]
    public void PushedPayload_SerializesKindAsAName()
    {
        var pushed = new NotificationResponse
        {
            Id = Guid.NewGuid(),
            Kind = NotificationKind.OrderPlaced,
            Title = "Order placed",
            Body = "We have your order.",
            SubjectId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            ReadAt = null,
        };

        var json = JsonSerializer.Serialize(pushed, PayloadOptions());

        // The same invariant NotificationResponse.Kind's doc comment states and
        // NotificationsEndpointTests proves for the REST path.
        json.Should().Contain("\"OrderPlaced\"");
        json.Should().NotContain("\"kind\":0");
    }

    [Fact]
    public void PushedPayload_UsesTheSamePropertyCasingAsRest()
    {
        // camelCase on both paths, so a client can reuse one type for the pushed object and the
        // one it reads back from the feed.
        var pushed = new NotificationResponse
        {
            Id = Guid.NewGuid(),
            Kind = NotificationKind.OrderShipped,
            Title = "Order shipped",
            Body = "On its way.",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(pushed, PayloadOptions());

        json.Should().Contain("\"kind\"");
        json.Should().NotContain("\"Kind\"");
    }
}
