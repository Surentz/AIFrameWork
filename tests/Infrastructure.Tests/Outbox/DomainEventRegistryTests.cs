using System.Text.Json;
using AiFramework.Application.Abstractions;
using AiFramework.Domain.Abstractions;
using AiFramework.Infrastructure.Outbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Tests.Outbox;

public sealed record Pinged(string Text) : IDomainEvent;

/// <summary>
/// Per-provider sink. NOT a static list: xUnit runs test classes in parallel, and two classes
/// share this handler — a static collection would race and produce flaky, order-dependent
/// failures that look like real bugs.
/// </summary>
public sealed class HandlerSink
{
    public ICollection<string> Seen { get; } = [];
}

public sealed class RecordingHandler(HandlerSink sink) : IDomainEventHandler<Pinged>
{
    public Task HandleAsync(Pinged domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        sink.Seen.Add($"{domainEvent.Text}:{context.Attempt}");
        return Task.CompletedTask;
    }
}

public sealed class DomainEventRegistryTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<HandlerSink>();
        services.AddDomainEvent<Pinged>("test.pinged");
        services.AddScoped<IDomainEventHandler<Pinged>, RecordingHandler>();
        services.AddSingleton<DomainEventRegistry>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void GetName_ForARegisteredEvent_ReturnsTheStableName()
    {
        using var provider = Build();

        provider.GetRequiredService<DomainEventRegistry>()
            .GetName(typeof(Pinged)).Should().Be("test.pinged");
    }

    [Fact]
    public void GetName_ForAnUnregisteredEvent_ThrowsNamingTheType()
    {
        using var provider = Build();

        var act = () => provider.GetRequiredService<DomainEventRegistry>().GetName(typeof(Order2));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Order2*");
    }

    [Fact]
    public async Task Dispatch_DeserialisesAndInvokesEveryHandler()
    {
        using var provider = Build();
        var sink = provider.GetRequiredService<HandlerSink>();
        var registry = provider.GetRequiredService<DomainEventRegistry>();
        registry.TryGet("test.pinged", out var descriptor).Should().BeTrue();
        if (descriptor is null)
        {
            throw new InvalidOperationException("TryGet returned true but descriptor was null.");
        }

        var payload = JsonSerializer.Serialize(new Pinged("hello"));
        await descriptor.Dispatch(
            provider, payload, new DomainEventContext(Guid.NewGuid(), 2), CancellationToken.None);

        sink.Seen.Should().ContainSingle().Which.Should().Be("hello:2");
    }

    [Fact]
    public void TryGet_ForAnUnknownName_ReturnsFalse()
    {
        using var provider = Build();

        provider.GetRequiredService<DomainEventRegistry>()
            .TryGet("nope", out _).Should().BeFalse();
    }

    private sealed record Order2 : IDomainEvent;
}
