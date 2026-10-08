using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiFramework.Infrastructure.ExternalSystems.Certificates;
using Duende.AccessTokenManagement;
using Duende.IdentityModel;
using Duende.IdentityModel.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiFramework.Infrastructure.ExternalSystems.Auth;

/// <summary>
/// Signs a private_key_jwt client assertion with the system's own certificate — the OCES3
/// certificate that also authenticates the TLS connection. Duende calls this for EVERY client;
/// it answers only for systems configured as PrivateKeyJwt and returns null for the rest.
/// </summary>
/// <remarks>
/// Audience is the authorization server's ISSUER, not its token endpoint: Duende's guidance after
/// CVE-2025-27370/27371. Sixty seconds of life and a fresh jti, so a captured assertion is worth
/// little. The assertion itself is never logged.
/// </remarks>
internal sealed partial class PrivateKeyJwtAssertionService(
    ICertificateProvider certificates,
    IOptionsMonitor<ExternalSystemsOptions> options,
    TimeProvider time,
    ILogger<PrivateKeyJwtAssertionService> logger) : IClientAssertionService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public Task<ClientAssertion?> GetClientAssertionAsync(
        ClientCredentialsClientName? clientName = null,
        TokenRequestParameters? parameters = null,
        CancellationToken ct = default)
    {
        if (clientName is null)
        {
            return Task.FromResult<ClientAssertion?>(null);
        }

        var name = clientName.Value.ToString();
        var system = options.CurrentValue.Find(name);
        if (system is not { Auth.Kind: ExternalSystemAuthKind.PrivateKeyJwt })
        {
            return Task.FromResult<ClientAssertion?>(null);
        }

        var loaded = certificates.GetCurrent(name);
        if (loaded.Certificate is null)
        {
            // Null makes the token request fail at the IdP (401), which the token check reports;
            // throwing here would surface as an unclassified exception inside Duende instead.
            LogNoCertificate(name, loaded.Problem ?? "no client certificate");
            return Task.FromResult<ClientAssertion?>(null);
        }

        var certificate = loaded.Certificate.Certificate;
        var now = time.GetUtcNow().UtcDateTime;
        var clientId = system.Auth.ClientId!; // validated non-empty for every Kind other than None.
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = system.Auth.Issuer,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + Lifetime,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtClaimTypes.Subject] = clientId,
                [JwtClaimTypes.JwtId] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(
                new X509SecurityKey(certificate, KeyIdFor(certificate)), AlgorithmFor(certificate)),
        };

        return Task.FromResult<ClientAssertion?>(new ClientAssertion
        {
            Type = OidcConstants.ClientAssertionTypes.JwtBearer,
            Value = new JsonWebTokenHandler().CreateToken(descriptor),
        });
    }

    /// <summary>
    /// base64url(SHA-256(SubjectPublicKeyInfo)) — the key id Keycloak derives for a certificate
    /// registered on a client ("jwt.credential.certificate") and then looks the assertion's kid
    /// up by. The default, the SHA-1 thumbprint X509SigningCredentials puts there, is answered
    /// with invalid_client, "Unable to load public key". ADR 0031.
    /// </summary>
    private static string KeyIdFor(X509Certificate2 certificate) =>
        Base64UrlEncoder.Encode(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    private static string AlgorithmFor(X509Certificate2 certificate)
    {
        using var ec = certificate.GetECDsaPublicKey();
        return ec is not null ? SecurityAlgorithms.EcdsaSha256 : SecurityAlgorithms.RsaSha256;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No client assertion for {System}: {Problem}")]
    private partial void LogNoCertificate(string system, string problem);
}
