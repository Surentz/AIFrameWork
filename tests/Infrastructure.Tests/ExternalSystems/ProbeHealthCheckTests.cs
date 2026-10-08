using System.Net;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.Tests.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class ProbeHealthCheckTests
{
    private static async Task<HealthReportEntry> ProbeAsync(Func<HttpRequestMessage, HttpResponseMessage> respond,
        string timeout = "00:00:05")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:Sim:BaseAddress"] = "https://partner.example/api",
            ["Systems:Sim:Probe:Path"] = "ping",
            ["Systems:Sim:Probe:Timeout"] = timeout,
        }));
        services.AddHttpClient(ExternalSystemNames.Probe("Sim"))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(respond));

        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => string.Equals(c.Name, "Sim", StringComparison.Ordinal));
        return report.Entries["Sim"];
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task CheckHealth_WhenTheProbeAnswersBelow500_IsHealthy(HttpStatusCode status)
    {
        var entry = await ProbeAsync(_ => new HttpResponseMessage(status));

        entry.Status.Should().Be(HealthStatus.Healthy);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CheckHealth_WhenTheProbeAnswers5xx_IsUnhealthy(HttpStatusCode status)
    {
        var entry = await ProbeAsync(_ => new HttpResponseMessage(status));

        entry.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealth_ProbesTheConfiguredPathUnderTheBasePath()
    {
        Uri? requested = null;

        await ProbeAsync(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        requested.Should().Be(new Uri("https://partner.example/api/ping"));
    }

    [Fact]
    public async Task CheckHealth_WhenTheConnectionFails_IsUnhealthyAndNamesOnlyTheCategory()
    {
        var entry = await ProbeAsync(_ => throw new HttpRequestException(
            HttpRequestError.ConnectionError, "Connection refused (partner.example:443)"));

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().Contain("ConnectionError").And.NotContain("partner.example");
    }

    [Fact]
    public async Task CheckHealth_WhenTheProbeOutlivesItsTimeout_IsDegraded()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:Sim:BaseAddress"] = "https://partner.example/",
            ["Systems:Sim:Probe:Timeout"] = "00:00:00.200",
        }));
        services.AddHttpClient(ExternalSystemNames.Probe("Sim"))
            .ConfigurePrimaryHttpMessageHandler(() => new HangingHandler());
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => string.Equals(c.Name, "Sim", StringComparison.Ordinal));

        report.Entries["Sim"].Status.Should().Be(HealthStatus.Degraded);
    }

    /// <summary>
    /// Never answers until cancelled. Must be ASYNC: a synchronous stub (StubHttpMessageHandler
    /// with a sleep) cannot be interrupted by the probe's CancellationToken, so it would answer
    /// late but successfully and the check would read Healthy.
    /// </summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable: the delay only ends by cancellation");
        }
    }
}
