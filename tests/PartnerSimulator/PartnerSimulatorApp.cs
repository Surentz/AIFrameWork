using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.PartnerSimulator;

public sealed class PartnerSimulatorOptions
{
    public required X509Certificate2 ServerCertificate { get; init; }

    /// <summary>The only root a client certificate may chain to. Intermediates must come from the client.</summary>
    public required X509Certificate2 TrustedClientRoot { get; init; }

    /// <summary>A Keycloak realm URL. Null: /echo needs no token.</summary>
    public string? JwtAuthority { get; init; }

    /// <summary>The HTTPS (mTLS) port. 0 picks a free one — what the in-process tests use.</summary>
    public int HttpsPort { get; init; }

    /// <summary>
    /// A plain-HTTP port serving only <c>/health</c>, for a readiness check that holds no client
    /// certificate (Playwright's webServer). Null: none.
    /// </summary>
    public int? HealthPort { get; init; }

    /// <summary>
    /// A partner's own routes, mapped on the mTLS listener beside /ping and /echo: how a test
    /// stands in for one real partner's API (the /external-system command's adapter tests).
    /// Null: only the built-in routes.
    /// </summary>
    public Action<IEndpointRouteBuilder>? Endpoints { get; init; }
}

public sealed record SimulatorRequest(string? Authorization, string? ClientCertificateSubject);

public sealed record EchoResponse(string? ClientCertificateSubject, string? Subject);

/// <summary>
/// An mTLS partner on a loopback port, in-process, with real TLS — TestServer would skip the
/// handshake that is half of what the tests are about. Tests script /echo's next statuses and
/// read back what it received. The e2e run and the dev loop start one on fixed ports through
/// Program.cs's <c>serve</c>.
/// </summary>
public sealed class PartnerSimulatorApp : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<HttpStatusCode> _script = new();
    private readonly ConcurrentQueue<SimulatorRequest> _echoRequests = new();
    private int _pingRequests;

    private PartnerSimulatorApp(WebApplication app) => _app = app;

    public Uri BaseAddress { get; private set; } = new("https://127.0.0.1/");

    public IReadOnlyList<SimulatorRequest> EchoRequests => [.. _echoRequests];

    public int PingRequests => Volatile.Read(ref _pingRequests);

    /// <summary>The next /echo answers with this status instead of 200, once per call.</summary>
    public void EnqueueEchoStatus(HttpStatusCode status) => _script.Enqueue(status);

    private static void ConfigureTls(KestrelServerOptions kestrel, PartnerSimulatorOptions options)
    {
        if (options.HealthPort is { } healthPort)
        {
            kestrel.Listen(IPAddress.Loopback, healthPort);
        }

        kestrel.Listen(IPAddress.Loopback, options.HttpsPort, listen =>
            listen.UseHttps(https =>
            {
                https.ServerCertificate = options.ServerCertificate;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

                // Custom root trust for the CLIENT's chain. SslStream adds the intermediates the
                // client sent to ExtraStore itself, so this fails exactly when the client sends
                // only its leaf — no callback, so nothing can accept by accident.
                https.OnAuthenticate = (_, ssl) =>
                {
                    var policy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                        RevocationMode = X509RevocationMode.NoCheck,
                    };
                    policy.CustomTrustStore.Add(options.TrustedClientRoot);
                    ssl.CertificateChainPolicy = policy;
                };
                https.ClientCertificateValidation = (_, _, errors) => errors == SslPolicyErrors.None;
            }));
    }

    private static void AddJwtBearer(IServiceCollection services, string authority)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = authority;
                jwt.RequireHttpsMetadata = false;
                // Keycloak's client-credentials tokens carry aud "account" unless a mapper is
                // added; the simulator checks issuer, signature and lifetime only.
                jwt.TokenValidationParameters.ValidateAudience = false;
            });
        services.AddAuthorization();
    }

    public static async Task<PartnerSimulatorApp> StartAsync(
        PartnerSimulatorOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => ConfigureTls(kestrel, options));
        if (options.JwtAuthority is not null)
        {
            AddJwtBearer(builder.Services, options.JwtAuthority);
        }

        var app = builder.Build();
        var simulator = new PartnerSimulatorApp(app);

        if (options.JwtAuthority is not null)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.MapGet("/ping", () =>
        {
            Interlocked.Increment(ref simulator._pingRequests);
            return Results.Text("pong");
        });

        if (options.HealthPort is { } healthPort)
        {
            app.MapGet("/health", () => Results.Text("ok")).RequireHost($"*:{healthPort}");
        }

        var echo = app.MapGet("/echo", (HttpContext context) =>
        {
            var subject = context.Connection.ClientCertificate?.Subject;
            simulator._echoRequests.Enqueue(new SimulatorRequest(
                context.Request.Headers.Authorization.ToString() is { Length: > 0 } auth ? auth : null,
                subject));

            return simulator._script.TryDequeue(out var status)
                ? Results.StatusCode((int)status)
                : Results.Json(new EchoResponse(subject, context.User.FindFirst("sub")?.Value));
        });

        if (options.JwtAuthority is not null)
        {
            echo.RequireAuthorization();
        }

        options.Endpoints?.Invoke(app);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        // The HTTPS one: with a health port, a plain-HTTP address sits beside it.
        var address = (app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel exposes no server addresses.")).Addresses
            .Single(candidate => candidate.StartsWith("https", StringComparison.Ordinal));
        simulator.BaseAddress = new UriBuilder(address).Uri;
        return simulator;
    }

    /// <summary>Completes when the host is told to stop (Ctrl+C, or the process being ended).</summary>
    public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
