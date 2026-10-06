using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AiFramework.PartnerSimulator;

/// <summary>
/// A throwaway root → intermediate → leaf PKI, generated in memory. The intermediate is the point:
/// the simulator trusts only the root, so a handshake succeeds only if the CLIENT sends the
/// intermediate with its leaf — which is what an OCES3 PFX needs on Linux, and what a Windows
/// machine store can otherwise paper over.
/// </summary>
/// <remarks>Never persist one of these anywhere but a git-ignored folder. See ADR 0031.</remarks>
public sealed class TestPki : IDisposable
{
    private readonly RSA _intermediateKey;

    private TestPki(X509Certificate2 root, X509Certificate2 intermediate, RSA intermediateKey)
    {
        Root = root;
        Intermediate = intermediate;
        _intermediateKey = intermediateKey;
    }

    public X509Certificate2 Root { get; }

    public X509Certificate2 Intermediate { get; }

    public string RootPem => Root.ExportCertificatePem();

    public static TestPki Create(string name = "AiFramework Test")
    {
        var now = DateTimeOffset.UtcNow;

        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            $"CN={name} Root CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

        var intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest(
            $"CN={name} Intermediate CA", intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        intermediateRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));
        intermediateRequest.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        using var intermediatePublic = intermediateRequest.Create(
            root, now.AddDays(-1), now.AddYears(4), NewSerial());
        var intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);

        return new TestPki(root, intermediate, intermediateKey);
    }

    /// <summary>A client-authentication leaf issued by the intermediate.</summary>
    public X509Certificate2 IssueClient(string commonName, DateTimeOffset? notAfter = null) =>
        Issue($"CN={commonName}", "1.3.6.1.5.5.7.3.2", notAfter, sans: null);

    /// <summary>A server-authentication leaf for localhost and 127.0.0.1.</summary>
    public X509Certificate2 IssueServer()
    {
        var sans = new SubjectAlternativeNameBuilder();
        sans.AddDnsName("localhost");
        sans.AddIpAddress(System.Net.IPAddress.Loopback);
        return Issue("CN=localhost", "1.3.6.1.5.5.7.3.1", notAfter: null, sans);
    }

    /// <summary>Leaf (with its key) plus the intermediate, the shape of a real OCES3 PFX.</summary>
    public byte[] ExportPfx(X509Certificate2 leaf, string? password)
    {
        ArgumentNullException.ThrowIfNull(leaf);

        var collection = new X509Certificate2Collection
        {
            leaf,
            X509CertificateLoader.LoadCertificate(Intermediate.RawData),
        };
        return collection.Export(X509ContentType.Pkcs12, password)
            ?? throw new InvalidOperationException("PKCS#12 export returned nothing.");
    }

    /// <summary>
    /// Round-trips a generated certificate through PKCS#12. Windows' TLS stack cannot use the
    /// ephemeral key <c>CopyWithPrivateKey</c> produces ("No credentials are available in the
    /// security package"); a reloaded one it can. Harmless elsewhere.
    /// </summary>
    public static X509Certificate2 Usable(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null);
    }

    /// <summary>The files scripts/new-dev-certs.ps1 promises: ca.pem, client.pfx/.pass, server.pfx/.pass.</summary>
    public void WriteDevFiles(string directory)
    {
        Directory.CreateDirectory(directory);
        const string devPfxKey = "dev-only";

        File.WriteAllText(Path.Combine(directory, "ca.pem"), RootPem);
        File.WriteAllBytes(Path.Combine(directory, "client.pfx"), ExportPfx(IssueClient("aiframework-dev-client"), devPfxKey));
        File.WriteAllText(Path.Combine(directory, "client.pass"), devPfxKey);
        File.WriteAllBytes(Path.Combine(directory, "server.pfx"), ExportPfx(IssueServer(), devPfxKey));
        File.WriteAllText(Path.Combine(directory, "server.pass"), devPfxKey);
    }

    public void Dispose()
    {
        _intermediateKey.Dispose();
        Root.Dispose();
        Intermediate.Dispose();
    }

    private X509Certificate2 Issue(
        string subject, string extendedKeyUsageOid, DateTimeOffset? notAfter, SubjectAlternativeNameBuilder? sans)
    {
        var now = DateTimeOffset.UtcNow;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(extendedKeyUsageOid)], false));
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Intermediate, true, false));
        if (sans is not null)
        {
            request.CertificateExtensions.Add(sans.Build());
        }

        var expiry = notAfter ?? now.AddYears(2);
        var notBefore = expiry < now ? expiry.AddDays(-30) : now.AddDays(-1);
        using var issued = request.Create(Intermediate, notBefore, expiry, NewSerial());
        return issued.CopyWithPrivateKey(key);
    }

    private static byte[] NewSerial() => RandomNumberGenerator.GetBytes(16);
}
