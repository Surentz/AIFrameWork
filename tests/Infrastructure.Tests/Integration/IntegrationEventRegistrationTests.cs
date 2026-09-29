using AiFramework.Application.IntegrationEvents;
using AiFramework.Infrastructure.Integration;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Integration;

/// <summary>
/// The completeness net for outbound contracts, like JobRegistrationTests is for jobs: a contract
/// with no route is published into nothing, silently.
/// </summary>
public sealed class IntegrationEventRegistrationTests
{
    [Fact]
    public void EveryIntegrationEvent_IsRegisteredForOutboundRouting()
    {
        var declared = typeof(IIntegrationEvent).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(IIntegrationEvent)));

        var registered = IntegrationEventRegistration.Outbound.Select(d => d.EventType);

        registered.Should().BeEquivalentTo(declared);
    }

    [Fact]
    public void EveryRegistration_HasAUniqueTypeName()
    {
        IntegrationEventRegistration.Outbound.Select(d => d.TypeName).Should().OnlyHaveUniqueItems();
    }
}
