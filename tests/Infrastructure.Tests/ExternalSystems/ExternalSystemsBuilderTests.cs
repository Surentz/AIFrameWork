using System.Net;
using System.Text;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.Resilience;
using AiFramework.Infrastructure.Tests.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using NSubstitute;
using Refit;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class ExternalSystemsBuilderTests
{
    private readonly ITrafficRecorder _traffic = Substitute.For<ITrafficRecorder>();

    private ExternalSystemsBuilder Builder(IServiceCollection services, IDictionary<string, string?>? extra = null)
    {
        var config = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:Sim:BaseAddress"] = "https://partner.example/",
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>(StringComparer.Ordinal))
        {
            config[key] = value;
        }

        services.AddLogging();
        services.AddSingleton(_traffic);
        services.AddResilience();
        return services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(config));
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

    // Spec §3: a timeout is Faulted. The attempt counter sits inside the standard handler, where
    // its token is Polly's attempt timeout, so a timed-out attempt looks like a cancellation.
    [Fact]
    public async Task Send_WhenEveryAttemptTimesOut_RecordsEachAttemptAsFaulted()
    {
        var services = new ServiceCollection();
        Builder(services, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:Sim:Resilience:AttemptTimeout"] = "00:00:00.200",
            ["Systems:Sim:Resilience:TotalRequestTimeout"] = "00:00:10",
            ["Systems:Sim:Resilience:MaxRetryAttempts"] = "1",
            ["Systems:Sim:Resilience:BaseDelay"] = "00:00:00.010",
        }).AddClient<ISimulatorApi>("Sim");
        using var partner = new HangingHandler();
        services.AddHttpClient(UniqueName.ForType<ISimulatorApi>()).ConfigurePrimaryHttpMessageHandler(() => partner);
        await using var provider = services.BuildServiceProvider();

        await Record.ExceptionAsync(async () => _ = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None));

        _traffic.Received(2).Record(TrafficKind.OutboundAttempt, "Sim", TrafficOutcome.Faulted, Arg.Any<long>());
        _traffic.Received(1).Record(TrafficKind.Outbound, "Sim", TrafficOutcome.Faulted, Arg.Any<long>());
    }

    [Fact]
    public async Task Send_WhenTheCallerCancelsMidAttempt_RecordsNothing()
    {
        var services = new ServiceCollection();
        Builder(services).AddClient<ISimulatorApi>("Sim");
        using var partner = new HangingHandler();
        services.AddHttpClient(UniqueName.ForType<ISimulatorApi>()).ConfigurePrimaryHttpMessageHandler(() => partner);
        await using var provider = services.BuildServiceProvider();
        using var caller = new CancellationTokenSource();

        var call = provider.GetRequiredService<ISimulatorApi>().EchoAsync(caller.Token);
        await partner.Entered.Task;
        await caller.CancelAsync();
        var thrown = await Record.ExceptionAsync(async () => _ = await call);

        thrown.Should().BeAssignableTo<OperationCanceledException>();
        _traffic.DidNotReceiveWithAnyArgs().Record(default, default!, default, default);
    }

    // Configuration keys are case-insensitive, named options are not: an environment variable
    // spells the key PARTNERSIM, the code says "PartnerSim", and Duende's client is registered
    // under the configuration's spelling.
    [Fact]
    public async Task AddClient_WithADifferentlyCasedName_SendsTheSystemsToken()
    {
        var services = new ServiceCollection();
        var captured = new List<string?>();
        AddCasedSystem(services, captured);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        captured.Should().Equal("Bearer cased-token");
    }

    [Fact]
    public async Task AddClient_WithADifferentlyCasedName_RecordsTrafficUnderTheConfiguredName()
    {
        var services = new ServiceCollection();
        AddCasedSystem(services, []);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        _traffic.Received(1).Record(TrafficKind.Outbound, "PARTNERSIM", TrafficOutcome.Succeeded, Arg.Any<long>());
    }

    [Fact]
    public void AddClient_WithADifferentlyCasedName_ReportsTheConfiguredName()
    {
        var client = AddCasedSystem(new ServiceCollection(), []);

        client.SystemName.Should().Be("PARTNERSIM");
    }

    // HttpStandardResilienceOptions is validated on start and requires SamplingDuration to be at
    // least twice AttemptTimeout; the 30 s default would stop the host at any AttemptTimeout > 15 s.
    [Fact]
    public void AddClient_WithAnAttemptTimeoutOverHalfTheDefaultSamplingDuration_PassesStartupValidation()
    {
        var services = new ServiceCollection();
        Builder(services, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:Sim:Resilience:AttemptTimeout"] = "00:00:20",
            ["Systems:Sim:Resilience:TotalRequestTimeout"] = "00:01:00",
        }).AddClient<ISimulatorApi>("Sim");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate(); // what a host runs on start.

        // The standard handler names its options "<client name>-standard".
        provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get($"{UniqueName.ForType<ISimulatorApi>()}-standard")
            .CircuitBreaker.SamplingDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(40));
    }

    private ExternalSystemClientBuilder<ISimulatorApi> AddCasedSystem(IServiceCollection services, List<string?> authorizations)
    {
        var client = Builder(services, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:PARTNERSIM:BaseAddress"] = "https://partner.example/",
            ["Systems:PARTNERSIM:Auth:Kind"] = "ClientSecret",
            ["Systems:PARTNERSIM:Auth:TokenEndpoint"] = "https://idp.example/token",
            ["Systems:PARTNERSIM:Auth:ClientId"] = "client",
            ["Systems:PARTNERSIM:Auth:ClientSecretFile"] = "absent",
        }).AddClient<ISimulatorApi>("PartnerSim");

        services.AddHttpClient(UniqueName.ForType<ISimulatorApi>()).ConfigurePrimaryHttpMessageHandler(() =>
            new StubHttpMessageHandler(request =>
            {
                authorizations.Add(request.Headers.Authorization?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            }));
        services.AddHttpClient(ExternalSystemNames.TokenBackchannel("PARTNERSIM")).ConfigurePrimaryHttpMessageHandler(() =>
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"cased-token","token_type":"Bearer","expires_in":300}""", Encoding.UTF8, "application/json"),
            }));
        return client;
    }

    /// <summary>A partner that never answers: each attempt ends only when its token is cancelled.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable: an infinite delay ends only by cancellation");
        }
    }
}
