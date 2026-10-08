using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.ExternalSystems.Auth;
using AiFramework.Infrastructure.ExternalSystems.Certificates;
using AiFramework.PartnerSimulator;
using Duende.AccessTokenManagement;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class PrivateKeyJwtAssertionServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-jwt-").FullName;
    private readonly TestPki _pki = TestPki.Create();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public void Dispose()
    {
        _pki.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private (PrivateKeyJwtAssertionService Service, System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate) Build(
        ExternalSystemAuthKind kind = ExternalSystemAuthKind.PrivateKeyJwt)
    {
        var leaf = _pki.IssueClient("jwt-client");
        var path = Path.Combine(_directory, "client.pfx");
        File.WriteAllBytes(path, _pki.ExportPfx(leaf, password: null));
        var system = new ExternalSystemOptions
        {
            BaseAddress = "https://partner.example/",
            ClientCertificate = new ClientCertificateOptions { Path = path },
        };
        system.Auth.Kind = kind;
        system.Auth.ClientId = "jwt-client";
        system.Auth.Issuer = "https://idp.example/realms/aiframework";
        system.Auth.TokenEndpoint = "https://idp.example/realms/aiframework/protocol/openid-connect/token";
        var options = new ExternalSystemsOptions();
        options.Systems["Sim"] = system;
        var monitor = new StaticOptionsMonitor(options);
        var provider = new FileCertificateProvider(monitor, _clock, NullLogger<FileCertificateProvider>.Instance);
        return (new PrivateKeyJwtAssertionService(provider, monitor, _clock, NullLogger<PrivateKeyJwtAssertionService>.Instance), leaf);
    }

    [Fact]
    public async Task GetClientAssertion_ForAPrivateKeyJwtSystem_IsSignedByItsCertificateForTheIssuer()
    {
        var (service, certificate) = Build();

        var assertion = await service.GetClientAssertionAsync(ClientCredentialsClientName.Parse("Sim"));

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(assertion!.Value, new TokenValidationParameters
        {
            ValidIssuer = "jwt-client",
            ValidAudience = "https://idp.example/realms/aiframework",
            IssuerSigningKey = new X509SecurityKey(certificate),
            ValidateLifetime = false,
        }); // assertion is non-null for a PrivateKeyJwt system; the next line fails loudly if not.
        validation.IsValid.Should().BeTrue(validation.Exception?.Message);
    }

    [Fact]
    public async Task GetClientAssertion_LivesAtMostSixtySecondsAndCarriesSubAndJti()
    {
        var (service, _) = Build();

        var assertion = await service.GetClientAssertionAsync(ClientCredentialsClientName.Parse("Sim"));

        var token = new JsonWebToken(assertion!.Value); // non-null: PrivateKeyJwt system.
        (token.ValidTo - token.IssuedAt).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
        token.Subject.Should().Be("jwt-client");
        token.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetClientAssertion_ForAClientSecretSystem_ReturnsNull()
    {
        var (service, _) = Build(ExternalSystemAuthKind.ClientSecret);

        var assertion = await service.GetClientAssertionAsync(ClientCredentialsClientName.Parse("Sim"));

        assertion.Should().BeNull();
    }
}
