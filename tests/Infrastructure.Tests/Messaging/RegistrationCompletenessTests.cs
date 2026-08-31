using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ApplicationMarker = AiFramework.Application.AssemblyMarker;

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
    public void EveryCommand_ImplementsICommandExactlyOnce()
    {
        var multiple = Implementing(typeof(ICommand<>))
            .Where(t => t.GetInterfaces().Count(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)) > 1);

        multiple.Should().BeEmpty(
            "TResponse is inferred from the argument; two ICommand<> interfaces make it ambiguous");
    }
}
