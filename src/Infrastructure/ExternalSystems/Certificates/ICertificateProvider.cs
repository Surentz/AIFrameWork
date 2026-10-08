using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace AiFramework.Infrastructure.ExternalSystems.Certificates;

/// <summary>A system's current client certificate, or why there is none.</summary>
internal interface ICertificateProvider
{
    public CertificateLoadResult GetCurrent(string systemName);
}

internal sealed record CertificateLoadResult(LoadedClientCertificate? Certificate, string? Problem)
{
    public static CertificateLoadResult NotConfigured { get; } = new(null, null);

    public static CertificateLoadResult Loaded(LoadedClientCertificate certificate) => new(certificate, null);

    public static CertificateLoadResult Failed(string problem) => new(null, problem);
}

internal sealed class LoadedClientCertificate(X509Certificate2 certificate, SslStreamCertificateContext context)
{
    public X509Certificate2 Certificate { get; } = certificate;

    /// <summary>Built with the PFX's intermediates, so the handshake sends the full chain on Linux.</summary>
    public SslStreamCertificateContext Context { get; } = context;

    public DateTimeOffset NotAfter { get; } = new(certificate.NotAfter.ToUniversalTime());
}
