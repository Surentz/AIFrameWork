using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiFramework.Infrastructure.ExternalSystems.Certificates;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.ExternalSystems.Http;

/// <summary>
/// Builds the innermost handler of every external-system client. Server trust is a
/// CertificateChainPolicy with CustomRootTrust — never a validation callback, so "accept any
/// certificate" cannot be written here (NoAcceptAnyCertificateTests).
/// </summary>
internal sealed class ExternalSystemHandlerFactory(
    ICertificateProvider certificates,
    IOptionsMonitor<ExternalSystemsOptions> options,
    TimeProvider time)
{
    public HttpMessageHandler CreatePrimaryHandler(string systemName)
    {
        var system = options.CurrentValue.Find(systemName);
        if (system is null)
        {
            return new FailFastHandler(systemName, "it is not configured");
        }

        var ssl = new SslClientAuthenticationOptions();
        if (TryPresentCertificate(systemName, system, ssl) is { } certificateProblem)
        {
            return new FailFastHandler(systemName, certificateProblem);
        }

        if (system.ServerTrust is not null)
        {
            var roots = new X509Certificate2Collection();
            try
            {
                roots.ImportFromPemFile(system.ServerTrust.CaBundlePath);
            }
            catch (IOException)
            {
                return new FailFastHandler(systemName, "the server trust bundle could not be read");
            }
            catch (UnauthorizedAccessException)
            {
                return new FailFastHandler(systemName, "the server trust bundle could not be read");
            }
            catch (CryptographicException)
            {
                return new FailFastHandler(systemName, "the server trust bundle is not valid PEM");
            }

            if (roots.Count == 0)
            {
                return new FailFastHandler(systemName, "the server trust bundle contains no certificates");
            }

            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = system.ServerTrust.CheckRevocation
                    ? X509RevocationMode.Online
                    : X509RevocationMode.NoCheck,
            };
            policy.CustomTrustStore.AddRange(roots);
            ssl.CertificateChainPolicy = policy;
        }

        return new SocketsHttpHandler { SslOptions = ssl };
    }

    /// <summary>
    /// The token endpoint's handler: the same client certificate (for Keycloak's X.509
    /// authenticator and RFC 8705 certificate-bound tokens), OS trust for the IdP.
    /// </summary>
    public HttpMessageHandler CreateTokenEndpointHandler(string systemName)
    {
        var system = options.CurrentValue.Find(systemName);
        if (system is null)
        {
            return new FailFastHandler(systemName, "it is not configured");
        }

        var ssl = new SslClientAuthenticationOptions();
        return TryPresentCertificate(systemName, system, ssl) is { } problem
            ? new FailFastHandler(systemName, problem)
            : new SocketsHttpHandler { SslOptions = ssl };
    }

    /// <returns>Null when the certificate is attached or none is configured; otherwise why not.</returns>
    private string? TryPresentCertificate(string systemName, ExternalSystemOptions system, SslClientAuthenticationOptions ssl)
    {
        if (system.ClientCertificate is null)
        {
            return null;
        }

        var result = certificates.GetCurrent(systemName);
        if (result.Certificate is null)
        {
            return result.Problem ?? "no client certificate is available";
        }

        if (result.Certificate.NotAfter <= time.GetUtcNow())
        {
            return $"the client certificate expired on {result.Certificate.NotAfter:yyyy-MM-dd}";
        }

        ssl.ClientCertificateContext = result.Certificate.Context;
        return null;
    }
}
