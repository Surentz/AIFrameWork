using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.ExternalSystems.Certificates;

/// <summary>
/// Loads each system's PFX from its mounted file and re-reads it every <see cref="RecheckInterval"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Polling, not FileSystemWatcher.</b> Kubernetes updates a mounted Secret by swapping a
/// symlink, which file events do not reliably report. Re-reading a few kilobytes every two
/// minutes, and reloading only when the content hash changes, is cheap and always right.
/// IHttpClientFactory rebuilds primary handlers every two minutes by default, so a rotated
/// certificate reaches new connections without a restart.
/// </para>
/// <para>
/// <b>Old certificates are never disposed.</b> A handler built before a rotation may still be
/// mid-handshake with one; the garbage collector is the only owner that knows when it is done.
/// </para>
/// </remarks>
internal sealed partial class FileCertificateProvider(
    IOptionsMonitor<ExternalSystemsOptions> options,
    TimeProvider time,
    ILogger<FileCertificateProvider> logger) : ICertificateProvider
{
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public CertificateLoadResult GetCurrent(string systemName)
    {
        var configured = options.CurrentValue.Find(systemName)?.ClientCertificate;
        if (configured is null)
        {
            return CertificateLoadResult.NotConfigured;
        }

        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_entries.TryGetValue(systemName, out var entry) && now - entry.CheckedAt < RecheckInterval)
            {
                return entry.Result;
            }

            var refreshed = Refresh(systemName, configured, entry);
            _entries[systemName] = refreshed with { CheckedAt = now };
            return refreshed.Result;
        }
    }

    private Entry Refresh(string systemName, ClientCertificateOptions configured, Entry? previous)
    {
        byte[] bytes;
        string? password;
        try
        {
            bytes = File.ReadAllBytes(configured.Path);
            password = configured.PasswordFile is null
                ? null
                : File.ReadAllText(configured.PasswordFile).TrimEnd('\r', '\n');
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Failed(systemName, "client certificate file not found");
        }
        catch (IOException exception)
        {
            return Failed(systemName, $"client certificate could not be read: {exception.GetType().Name}");
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(systemName, "client certificate could not be read: access denied");
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (previous is not null && string.Equals(previous.Hash, hash, StringComparison.Ordinal))
        {
            return previous;
        }

        return Load(systemName, bytes, password, hash);
    }

    private Entry Load(string systemName, byte[] bytes, string? password, string hash)
    {
        X509Certificate2Collection collection;
        try
        {
            collection = X509CertificateLoader.LoadPkcs12Collection(bytes, password);
        }
        catch (CryptographicException exception)
        {
            // The type, never the message: some platforms put the file path in it.
            return Failed(systemName, $"client certificate could not be read: {exception.GetType().Name}");
        }

        var leaf = collection.FirstOrDefault(c => c.HasPrivateKey);
        if (leaf is null)
        {
            return Failed(systemName, "client certificate file holds no private key");
        }

        var intermediates = new X509Certificate2Collection();
        intermediates.AddRange(collection.Where(c => !ReferenceEquals(c, leaf)).ToArray());
        SslStreamCertificateContext context;
        try
        {
            context = SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
        }
        catch (CryptographicException exception)
        {
            // Caught so it never escapes CreatePrimaryHandler, which reaches this through
            // GetCurrent. The type, never the message, as above.
            return Failed(systemName, $"client certificate chain could not be prepared: {exception.GetType().Name}");
        }

        var loaded = new LoadedClientCertificate(leaf, context);

        LogLoaded(systemName, leaf.Subject, leaf.Thumbprint, loaded.NotAfter);
        return new Entry(CertificateLoadResult.Loaded(loaded), hash, default);
    }

    private Entry Failed(string systemName, string problem)
    {
        LogProblem(systemName, problem);
        return new Entry(CertificateLoadResult.Failed(problem), Hash: null, default);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Loaded the client certificate for {System}: {Subject}, thumbprint {Thumbprint}, expires {NotAfter:o}")]
    private partial void LogLoaded(string system, string subject, string thumbprint, DateTimeOffset notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No usable client certificate for {System}: {Problem}")]
    private partial void LogProblem(string system, string problem);

    private sealed record Entry(CertificateLoadResult Result, string? Hash, DateTimeOffset CheckedAt);
}
