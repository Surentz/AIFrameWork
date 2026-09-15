using AiFramework.Application.Abstractions;
using AiFramework.Domain.Abstractions;
using AiFramework.Infrastructure.Messaging;
using AiFramework.Infrastructure.Outbox;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Wolverine;
using ApplicationMarker = AiFramework.Application.AssemblyMarker;
using DomainMarker = AiFramework.Domain.AssemblyMarker;

namespace AiFramework.Infrastructure.Tests.Messaging;

/// <summary>
/// AddCommand/AddQuery are explicit, reflection-free registrations - §6.2 trades away
/// compile-time safety for a missing one to get that. This test buys the safety back by
/// reflecting over the Application assembly (never Infrastructure's) so a forgotten
/// registration fails a test instead of a request. "ApplicationMarker" disambiguates from
/// AiFramework.Infrastructure.AssemblyMarker, which this file's enclosing namespace
/// (AiFramework.Infrastructure.Tests.Messaging -> ... -> AiFramework.Infrastructure) would
/// otherwise bind the unqualified name to.
/// </summary>
public sealed class RegistrationCompletenessTests
{
    private static Type[] Implementing(Type openGeneric) =>
        ApplicationMarker.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                && t.GetInterfaces().Any(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric))
            .ToArray();

    /// <summary>The T in every non-abstract AbstractValidator&lt;T&gt; subclass in Application.</summary>
    private static Type[] Validated() =>
        ApplicationMarker.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                && t.BaseType is { IsGenericType: true }
                && t.BaseType.GetGenericTypeDefinition() == typeof(AbstractValidator<>))
            .Select(t => t.BaseType!.GetGenericArguments()[0])
            .ToArray();

    /// <summary>
    /// Every non-abstract Application type carrying a marker interface. ICacheable and
    /// IInvalidatesCache are plain markers rather than open generics, so the Implementing()
    /// scan above cannot reach them.
    /// </summary>
    private static Type[] Marked(Type marker) =>
        ApplicationMarker.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                && marker.IsAssignableFrom(t))
            .ToArray();

    [Fact]
    public void AddMessaging_RegistersEveryCommandInTheApplicationAssembly()
    {
        var services = new ServiceCollection();
        services.AddMessaging();
        var registered = services
            .Select(d => d.ImplementationInstance)
            .OfType<CommandDescriptor>()
            .Select(d => d.CommandType)
            .ToHashSet();

        var unregistered = Implementing(typeof(ICommand<>)).Where(t => !registered.Contains(t));

        unregistered.Should().BeEmpty(
            "every ICommand<> needs an AddCommand<> call or it fails at runtime, not at compile time");
    }

    [Fact]
    public void AddMessaging_RegistersEveryQueryInTheApplicationAssembly()
    {
        var services = new ServiceCollection();
        services.AddMessaging();
        var registered = services
            .Select(d => d.ImplementationInstance)
            .OfType<QueryDescriptor>()
            .Select(d => d.QueryType)
            .ToHashSet();

        var unregistered = Implementing(typeof(IQuery<>)).Where(t => !registered.Contains(t));

        unregistered.Should().BeEmpty(
            "every IQuery<> needs an AddQuery<> call or it fails at runtime, not at compile time");
    }

    [Fact]
    public void AddMessaging_RegistersAValidatorForEveryAbstractValidatorInTheApplicationAssembly()
    {
        var services = new ServiceCollection();
        services.AddMessaging();
        var registered = services
            .Where(d => d.ServiceType.IsGenericType
                && d.ServiceType.GetGenericTypeDefinition() == typeof(IValidator<>))
            .Select(d => d.ServiceType.GetGenericArguments()[0])
            .ToHashSet();

        var unregistered = Validated().Where(t => !registered.Contains(t));

        unregistered.Should().BeEmpty(
            "every AbstractValidator<T> needs an AddScoped<IValidator<T>, ...>() call " +
            "or validation for T silently never runs - absence is tolerated at dispatch " +
            "time, but not at registration time");
    }

    [Fact]
    public void AddMessaging_RegistersEveryDomainEventInTheDomainAssembly()
    {
        var services = new ServiceCollection();
        services.AddMessaging();
        var registered = services
            .Select(d => d.ImplementationInstance)
            .OfType<DomainEventDescriptor>()
            .Select(d => d.EventType)
            .ToHashSet();

        var domainEventTypes = DomainMarker.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                && typeof(IDomainEvent).IsAssignableFrom(t))
            .ToArray();

        // A completeness test over an empty set passes vacuously. If the alias above ever binds
        // to the wrong assembly (see the type comment), this scan silently finds nothing and the
        // BeEmpty() below turns green for the wrong reason - so the non-empty scan is asserted,
        // not eyeballed.
        domainEventTypes.Should().NotBeEmpty(
            "the scan must find at least OrderPlaced in AiFramework.Domain; an empty result " +
            "means DomainMarker resolved to the wrong assembly, not that there are no events");

        var unregistered = domainEventTypes.Where(t => !registered.Contains(t));

        unregistered.Should().BeEmpty(
            "an unregistered domain event throws at SaveChanges, taking the request down with it");
    }

    /// <summary>
    /// A descriptor proves the event has an AddDomainEvent&lt;T&gt; registration, but its
    /// DispatchAsync iterates GetServices&lt;IDomainEventHandler&lt;TEvent&gt;&gt;() - an empty
    /// sequence there runs the foreach body zero times with no exception and no log, and the
    /// outbox row still goes to Processed. This test composes the real root (AddInfrastructure,
    /// not AddMessaging alone) and forces every handler chain to actually construct, so a missing
    /// handler or a missing transitive dependency of a handler throws here instead of dropping an
    /// event silently in production.
    /// </summary>
    [Fact]
    public void AddInfrastructure_ResolvesAHandlerForEveryRegisteredDomainEvent()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Never connects: UseNpgsql does not touch the network at registration or at DbContext
        // construction. Only syntactic validity matters here.
        services.AddInfrastructure(
            "Host=localhost;Port=1;Database=unreachable;Username=none;Password=none");

        // Supplied by UseWolverine on the HOST builder, not by AddInfrastructure — so a test that
        // composes AddInfrastructure alone has to stand it in, the same way each host's Program.cs
        // supplies its own ICurrentUser. OrderPlacedConfirmationHandler is the first domain event
        // handler to reach a host-level service (IMessageBus, via IJobScheduler); without this line
        // the test fails with "Unable to resolve service for type 'Wolverine.IMessageBus'", which
        // is this composition boundary rather than the missing-handler trap the test guards.
        services.AddSingleton(Substitute.For<IMessageBus>());

        // ValidateScopes = true and resolving from a CreateScope() (not the root provider) both
        // matter: a scoped handler resolved from a validated root provider throws for a reason
        // unrelated to the trap this test guards, which would make the test pass or fail for the
        // wrong reason.
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var descriptors = services
            .Select(d => d.ImplementationInstance)
            .OfType<DomainEventDescriptor>()
            .ToArray();

        descriptors.Should().NotBeEmpty(
            "there is nothing for this test to prove if AddInfrastructure registered no domain events");

        foreach (var descriptor in descriptors)
        {
            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(descriptor.EventType);

            // GetServices constructs every registered handler - and its dependencies - right now.
            // A missing handler yields an empty sequence; a missing transitive dependency throws.
            var handlers = scope.ServiceProvider.GetServices(handlerType).ToArray();

            handlers.Should().NotBeEmpty(
                $"'{descriptor.EventType.Name}' is registered as a domain event but has no " +
                "resolvable IDomainEventHandler<> - DispatchAsync would iterate an empty " +
                "sequence and drop the event silently, with no exception and no log");
        }
    }

    [Fact]
    public void EveryCommand_ImplementsICommandExactlyOnce()
    {
        var multiple = Implementing(typeof(ICommand<>))
            .Where(t => t.GetInterfaces().Count(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)) > 1);

        multiple.Should().BeEmpty(
            "TResponse is inferred from the argument; two ICommand<> interfaces make it ambiguous");
    }

    [Fact]
    public void EveryQuery_ImplementsIQueryExactlyOnce()
    {
        var multiple = Implementing(typeof(IQuery<>))
            .Where(t => t.GetInterfaces().Count(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQuery<>)) > 1);

        multiple.Should().BeEmpty(
            "TResponse is inferred from the argument; two IQuery<> interfaces make it ambiguous");
    }

    [Fact]
    public void EveryCacheableType_IsARegisteredQuery()
    {
        var services = new ServiceCollection();
        services.AddMessaging();
        var registered = services
            .Select(d => d.ImplementationInstance)
            .OfType<QueryDescriptor>()
            .Select(d => d.QueryType)
            .ToHashSet();

        var cacheable = Marked(typeof(ICacheable));

        cacheable.Should().NotBeEmpty(
            "the scan must find at least GetOrders; an empty result means it is looking at the " +
            "wrong assembly, not that nothing is cacheable");

        var unregistered = cacheable.Where(t => !registered.Contains(t));

        unregistered.Should().BeEmpty(
            "an ICacheable type that is not a registered query is caching nothing - the marker " +
            "only takes effect inside AddQuery's dispatch delegate");
    }

    /// <summary>
    /// Behaviors.CachedAsync composes keys from typeof(TQuery).Name - the SIMPLE name, and
    /// deliberately so: IInvalidatesCache.Tags is written as nameof(TheQuery), which also yields
    /// the simple name, so switching the key prefix to FullName would stop it matching its own
    /// tag and eviction would silently reach nothing. The price of keeping simple names is the
    /// collision this test refuses to let anyone pay by accident.
    /// </summary>
    [Fact]
    public void EveryCacheableType_HasAUniqueSimpleName()
    {
        var cacheable = Marked(typeof(ICacheable));

        cacheable.Should().NotBeEmpty(
            "the scan must find at least GetOrders; an empty result means it is looking at the " +
            "wrong assembly, not that nothing is cacheable");

        var colliding = cacheable
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        colliding.Should().BeEmpty(
            "two ICacheable queries with the same simple name in different namespaces share a " +
            "cache key prefix, because CacheScope.Key is composed from typeof(TQuery).Name - so " +
            "with equal CacheKey values one query would be served a value of the other's " +
            "TResponse. Simple names are kept on purpose: IInvalidatesCache.Tags is " +
            "nameof(TheQuery), so a FullName key prefix would no longer match its tag and " +
            "eviction would drift out of agreement with the read side, silently. Rename one of " +
            "them rather than changing one side of that pair");
    }

    [Fact]
    public void EveryInvalidatingCommand_DeclaresAtLeastOneTag()
    {
        var invalidating = Marked(typeof(IInvalidatesCache));

        invalidating.Should().NotBeEmpty(
            "the scan must find at least PlaceOrder; an empty result means it is looking at the " +
            "wrong assembly");

        // Every command here is a record with a primary constructor, so Activator would want its
        // arguments. Reading the property off an uninitialized instance is enough, because Tags
        // is a computed property that touches no constructor state in any current implementation
        // - and if one ever does, this test says so by throwing rather than by passing wrongly.
        foreach (var type in invalidating)
        {
            var instance = System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(type);

            var tags = ((IInvalidatesCache)instance).Tags;

            tags.Should().NotBeEmpty(
                $"'{type.Name}' implements IInvalidatesCache but evicts nothing, which is a "
                + "no-op that reads as protection");
        }
    }
}
