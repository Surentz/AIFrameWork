using System.Net;
using System.Text;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.Tests.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

/// <summary>
/// What the token check's description may say, without Keycloak. The description reaches the
/// browser (spec §3), so it never names a host. TokenAcquisitionTests covers a real IdP.
/// </summary>
public sealed class TokenHealthCheckTests
{
    private static async Task<HealthReportEntry> TokenCheckAsync(string tokenEndpoint, Func<HttpResponseMessage>? idp = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:Sim:BaseAddress"] = "https://partner.example/",
            ["Systems:Sim:Auth:Kind"] = "ClientSecret",
            ["Systems:Sim:Auth:TokenEndpoint"] = tokenEndpoint,
            ["Systems:Sim:Auth:ClientId"] = "client",
            ["Systems:Sim:Auth:ClientSecretFile"] = "absent",
        }));
        if (idp is not null)
        {
            services.AddHttpClient(ExternalSystemNames.TokenBackchannel("Sim"))
                .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(_ => idp()));
        }

        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(c => string.Equals(c.Name, "Sim:token", StringComparison.Ordinal));
        return report.Entries["Sim:token"];
    }

    [Fact]
    public async Task CheckHealth_WhenTheTokenEndpointRefusesTheConnection_DescribesItWithoutTheHost()
    {
        var entry = await TokenCheckAsync("http://127.0.0.1:1/token");

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().NotContain("127.0.0.1");
    }

    [Fact]
    public async Task CheckHealth_WhenTheIdpAnswersWithAnOAuthError_DescribesItByItsCode()
    {
        var entry = await TokenCheckAsync("https://idp.example/token", () => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":"invalid_scope","error_description":"scope x is not allowed"}""", Encoding.UTF8, "application/json"),
        });

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().Be("Sim token unavailable: invalid_scope");
    }
}
