using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class ExternalSystemsBuilderTests
{
    private readonly ITrafficRecorder _traffic = Substitute.For<ITrafficRecorder>();

    private ExternalSystemsBuilder Builder(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton(_traffic);
        services.AddResilience();
        return services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:Sim:BaseAddress"] = "https://partner.example/",
        }));
    }

    [Fact]
    public async Task AddClient_ForAnUnconfiguredSystem_FailsAfterExactlyOneAttempt()
    {
        var services = new ServiceCollection();
        Builder(services).AddClient<ISimulatorApi>("Missing");
        await using var provider = services.BuildServiceProvider();

        var response = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        response.IsSuccessful.Should().BeFalse();
        _traffic.Received(1).Record(TrafficKind.OutboundAttempt, "Missing", TrafficOutcome.Faulted, Arg.Any<long>());
    }

    [Fact]
    public void AddClient_TheSameApiTypeTwice_Throws()
    {
        var builder = Builder(new ServiceCollection());
        builder.AddClient<ISimulatorApi>("Sim");

        var act = () => builder.AddClient<ISimulatorApi>("Other");

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{nameof(ISimulatorApi)}*");
    }
}
