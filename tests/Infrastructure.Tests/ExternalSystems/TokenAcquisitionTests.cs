using AiFramework.Infrastructure.ExternalSystems;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

[Collection(nameof(KeycloakCollection))]
public sealed class TokenAcquisitionTests(KeycloakFixture keycloak) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-token-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static async Task<HealthReportEntry> TokenCheckAsync(IDictionary<string, string?> auth)
    {
        var config = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Systems:Sim:BaseAddress"] = "https://partner.example/" };
        foreach (var (key, value) in auth)
        {
            config[$"Systems:Sim:{key}"] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(config));
        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => string.Equals(c.Name, "Sim:token", StringComparison.Ordinal));
        return report.Entries["Sim:token"];
    }

    [Fact]
    public async Task CheckHealth_WithTheRightClientSecret_IsHealthy()
    {
        var entry = await TokenCheckAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:Kind"] = "ClientSecret",
            ["Auth:TokenEndpoint"] = keycloak.TokenEndpoint,
            ["Auth:ClientId"] = KeycloakFixture.SecretClientId,
            ["Auth:ClientSecretFile"] = KeycloakFixture.WriteSecretFile(_directory),
        });

        entry.Status.Should().Be(HealthStatus.Healthy, entry.Description);
    }

    [Fact]
    public async Task CheckHealth_WithAWrongClientSecret_IsUnhealthyWithoutTheSecret()
    {
        var secret = Path.Combine(_directory, "wrong");
        await File.WriteAllTextAsync(secret, "not-the-secret");

        var entry = await TokenCheckAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:Kind"] = "ClientSecret",
            ["Auth:TokenEndpoint"] = keycloak.TokenEndpoint,
            ["Auth:ClientId"] = KeycloakFixture.SecretClientId,
            ["Auth:ClientSecretFile"] = secret,
        });

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().NotContain("not-the-secret");
    }

    [Fact]
    public async Task CheckHealth_WithAMissingSecretFile_IsUnhealthy()
    {
        var entry = await TokenCheckAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:Kind"] = "ClientSecret",
            ["Auth:TokenEndpoint"] = keycloak.TokenEndpoint,
            ["Auth:ClientId"] = KeycloakFixture.SecretClientId,
            ["Auth:ClientSecretFile"] = Path.Combine(_directory, "does-not-exist"),
        });

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Exception.Should().BeNull("a missing secret is a failed token result, not a throw");
    }

    [Fact]
    public async Task CheckHealth_WithAnEmptySecretFile_IsUnhealthy()
    {
        var secret = Path.Combine(_directory, "empty");
        await File.WriteAllTextAsync(secret, " \n");

        var entry = await TokenCheckAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:Kind"] = "ClientSecret",
            ["Auth:TokenEndpoint"] = keycloak.TokenEndpoint,
            ["Auth:ClientId"] = KeycloakFixture.SecretClientId,
            ["Auth:ClientSecretFile"] = secret,
        });

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Exception.Should().BeNull("an empty secret is a failed token result, not a throw");
    }

    // §5 probe 3: Keycloak's "Signed JWT" authenticator accepts our assertion with the
    // certificate registered on the client (not a JWKS URL), audience = the realm issuer.
    [Fact]
    public async Task CheckHealth_WithPrivateKeyJwt_IsHealthy()
    {
        var entry = await TokenCheckAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:Kind"] = "PrivateKeyJwt",
            ["Auth:TokenEndpoint"] = keycloak.TokenEndpoint,
            ["Auth:Issuer"] = keycloak.Issuer,
            ["Auth:ClientId"] = KeycloakFixture.JwtClientId,
            ["ClientCertificate:Path"] = keycloak.WriteJwtClientPfx(_directory),
        });

        entry.Status.Should().Be(HealthStatus.Healthy, entry.Description);
    }

    [Fact]
    public async Task CheckHealth_WithPrivateKeyJwtSignedByAnUnregisteredCertificate_IsUnhealthy()
    {
        using var stranger = AiFramework.PartnerSimulator.TestPki.Create("Stranger");
        var path = Path.Combine(_directory, "stranger.pfx");
        await File.WriteAllBytesAsync(path, stranger.ExportPfx(stranger.IssueClient(KeycloakFixture.JwtClientId), null));

        var entry = await TokenCheckAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:Kind"] = "PrivateKeyJwt",
            ["Auth:TokenEndpoint"] = keycloak.TokenEndpoint,
            ["Auth:Issuer"] = keycloak.Issuer,
            ["Auth:ClientId"] = KeycloakFixture.JwtClientId,
            ["ClientCertificate:Path"] = path,
        });

        entry.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
