namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// Every external system this host may call, keyed by name. The name is the configuration key,
/// the health-check name, the traffic name and the token client name — one string, so the
/// Monitoring page can put all four on one row. See ADR 0031.
/// </summary>
/// <remarks>
/// Secrets are FILE PATHS, never values: there is no property a secret's content could be
/// written into, so no appsettings file can hold one. Vault Secrets Operator mounts the files.
/// </remarks>
public sealed class ExternalSystemsOptions
{
    public const string SectionName = "ExternalSystems";

    /// <summary>Case-insensitive, like every configuration key.</summary>
    public IDictionary<string, ExternalSystemOptions> Systems { get; } =
        new Dictionary<string, ExternalSystemOptions>(StringComparer.OrdinalIgnoreCase);

    public ExternalSystemOptions? Find(string name) =>
        Systems.TryGetValue(name, out var system) ? system : null;

    /// <summary>
    /// The configured key that matches <paramref name="name"/> case-insensitively, in the
    /// configuration's own casing; <paramref name="name"/> itself when none does. Named options
    /// match ORDINALLY, and AddExternalSystems registers them under this spelling.
    /// </summary>
    internal string CanonicalName(string name) =>
        Systems.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) ?? name;
}

public sealed class ExternalSystemOptions
{
    /// <summary>Absolute http(s). Normalised to end in '/' when a client is built.</summary>
    public string BaseAddress { get; set; } = string.Empty;

    public ProbeOptions Probe { get; } = new();

    public ExternalSystemResilienceOptions Resilience { get; } = new();

    public ExternalSystemAuthOptions Auth { get; } = new();

    /// <summary>Null: this system is called without a client certificate.</summary>
    public ClientCertificateOptions? ClientCertificate { get; set; }

    /// <summary>Null: the server is validated against the OS trust store.</summary>
    public ServerTrustOptions? ServerTrust { get; set; }

    /// <summary>Within this long of NotAfter, the certificate check reports Degraded.</summary>
    public TimeSpan CertificateExpiryWarning { get; set; } = TimeSpan.FromDays(30);
}

public sealed class ProbeOptions
{
    /// <summary>GET or HEAD.</summary>
    public string Method { get; set; } = "GET";

    /// <summary>Relative to BaseAddress. Empty probes the base address itself.</summary>
    public string Path { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class ExternalSystemResilienceOptions
{
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>At least 1. Opting out of retry is the client builder's WithoutRetry, never 0 here.</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}

public enum ExternalSystemAuthKind
{
    None,
    ClientSecret,
    PrivateKeyJwt,
}

public enum ExternalSystemCredentialStyle
{
    AuthorizationHeader,
    PostBody,
}

public sealed class ExternalSystemAuthOptions
{
    public ExternalSystemAuthKind Kind { get; set; } = ExternalSystemAuthKind.None;

    public string? TokenEndpoint { get; set; }

    /// <summary>
    /// The authorization server's issuer URL — the AUDIENCE of a private_key_jwt assertion. Not
    /// the token endpoint: an assertion addressed to the token endpoint is the shape
    /// CVE-2025-27370/27371 exploit, and Duende's guidance is the issuer.
    /// </summary>
    public string? Issuer { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Space-separated, as OAuth sends it. Optional.</summary>
    public string? Scope { get; set; }

    public string? ClientSecretFile { get; set; }

    public ExternalSystemCredentialStyle CredentialStyle { get; set; } =
        ExternalSystemCredentialStyle.AuthorizationHeader;
}

public sealed class ClientCertificateOptions
{
    /// <summary>A PKCS#12 file holding the leaf with its key AND its intermediates.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>A file holding the PFX password. Null: the PFX has none.</summary>
    public string? PasswordFile { get; set; }
}

public sealed class ServerTrustOptions
{
    /// <summary>PEM file of root certificates this ONE system's server must chain to.</summary>
    public string CaBundlePath { get; set; } = string.Empty;

    /// <summary>Check revocation of the server chain. Off only for a test PKI with no CRL.</summary>
    public bool CheckRevocation { get; set; } = true;
}
