# External systems plumbing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the API and the worker call external systems over mTLS (OCES3) and OAuth 2.0 client credentials (Keycloak), with per-system retry, three health checks per system, and outbound traffic counting — proven against a simulated partner.

**Architecture:** Shared plumbing in `src/Infrastructure/ExternalSystems/`, configured per named system under `ExternalSystems:Systems:{Name}`. Every outbound client is built by one registration that fixes the handler chain: logical traffic → standard resilience → attempt traffic → Duende token handler → `SocketsHttpHandler` presenting the certificate. A simulated partner (`tests/PartnerSimulator`) with a throwaway PKI and a Testcontainers Keycloak exercise all of it over real TLS.

**Tech Stack:** .NET 10, `Microsoft.Extensions.Http.Resilience` 10.10.0 (already referenced), Refit.HttpClientFactory 16.3.0, Duende.AccessTokenManagement 4.2.0, Microsoft.IdentityModel.JsonWebTokens, Testcontainers.Keycloak 4.15.0, xUnit + FluentAssertions + NSubstitute.

**Spec:** [`docs/superpowers/specs/2026-10-06-external-systems-design.md`](../specs/2026-10-06-external-systems-design.md) — this plan is PR 2 of its §6. PR 3 (`feat(monitoring)`) gets its own plan once this one has merged, because §5's probes may change the interfaces it builds on.

**PR title:** `feat(integrations): call external systems over mTLS and OAuth 2.0` on branch `claude/external-systems`.

## Global Constraints

- Warnings are errors, in Debug and Release. Fix analyzer diagnostics; never suppress one without a `#pragma warning disable`/`restore` pair and a justification comment directly above it.
- Never `catch (Exception)` (CA1031). `throw;`, never `throw ex;`.
- Nullable is on. `!` needs an adjacent comment saying why null is impossible.
- Secrets are always **file paths** in options, never values. No certificate, private key, client secret or token is committed, logged, or put in a health-check description. Subject, thumbprint and `NotAfter` may be logged.
- `X509CertificateLoader` only — the `X509Certificate2` byte/file constructors raise SYSLIB0057.
- "Disable retry" is `options.Retry.ShouldHandle = _ => ValueTask.FromResult(false)`, never `MaxRetryAttempts = 0` (ADR 0014).
- Nothing inside a Polly-wrapped delegate returns a failed `Result` (`src/Infrastructure/CLAUDE.md`).
- `/health/ready` never runs a check tagged `external`, in either host.
- No certificate validation callback anywhere under `src/`: server trust is `SslClientAuthenticationOptions.CertificateChainPolicy` with `X509ChainTrustMode.CustomRootTrust`, never a callback that can return `true`.
- Test names `MethodName_Scenario_ExpectedOutcome`; arrange/act/assert separated by blank lines; one behaviour per test.
- Check `dotnet --list-sdks` shows 10.0.400 before trusting a local result as evidence about CI.
- Commit subjects in this PR use `feat(integrations): …` (docs commits inside it `docs(integrations): …`). End every commit message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A Windows-only green.** Windows can complete an mTLS handshake that Linux fails, by building the client chain from the machine store. The intermediate-CA test (Task 4, `Send_WithALeafIssuedByTheIntermediate_Succeeds`) only proves chain sending on Linux — CI's `backend` job is the evidence, not a local run. Do not merge on a local Windows green alone.
2. **A missing or broken certificate file must not stop the host.** Expected: host starts, that system's probe and certificate checks are Unhealthy, other systems unaffected. Pinned by Task 3 (`GetCurrent_WhenTheFileIsMissing_ReportsAProblem`) and Task 5 (`CheckHealth_WithTwoSystemsOneExpired_OnlyTheExpiredOneIsUnhealthy`).
3. **A configured-but-unreachable system must leave `/health/ready` at 200.** Pinned by Task 9 in both hosts, with a companion test proving the check exists and is Unhealthy, so the 200 is not vacuous.
4. **Outbound traffic must not inflate the existing traffic page.** The page's overall rate, error rate and series sum every kind. Expected: they keep counting inbound work only. Pinned by Task 6 (`SummarizeAsync_IgnoresOutboundKinds`).
5. **A 401 must trigger exactly one token refresh, not a loop and not zero.** Pinned by Task 8 (`Send_WhenThePartnerAnswers401Once_RefreshesTheTokenOnceAndSucceeds`).

---

## File Structure

| File | Responsibility |
|---|---|
| `tests/PartnerSimulator/AiFramework.PartnerSimulator.csproj` | Web SDK project, never deployed |
| `tests/PartnerSimulator/TestPki.cs` | Throwaway root → intermediate → leaf PKI; PFX/PEM export; `WriteDevFiles` |
| `tests/PartnerSimulator/PartnerSimulatorApp.cs` | In-process mTLS partner: `/ping`, `/echo`, scripted statuses, request log |
| `tests/PartnerSimulator/Program.cs` | `generate-certs <dir>` command for the dev script |
| `src/Infrastructure/ExternalSystems/ExternalSystemsOptions.cs` | Options tree for all systems |
| `src/Infrastructure/ExternalSystems/ExternalSystemsOptionsValidator.cs` | Startup validation of shape |
| `src/Infrastructure/ExternalSystems/ExternalSystemNames.cs` | Named-client and health-check name composition, in one place |
| `src/Infrastructure/ExternalSystems/ExternalSystemsRegistration.cs` | `AddExternalSystems`, returns `ExternalSystemsBuilder` |
| `src/Infrastructure/ExternalSystems/ExternalSystemsBuilder.cs` | `AddClient<TApi>(name)` |
| `src/Infrastructure/ExternalSystems/ExternalSystemClientBuilder.cs` | `.WithoutRetry(reason)`, `.WithAdapter<TPort, TAdapter>()` |
| `src/Infrastructure/ExternalSystems/ExternalSystemClientSettings.cs` | Named options set by the client builder (retry-disabled reason) |
| `src/Infrastructure/ExternalSystems/Certificates/ICertificateProvider.cs` | Port + `CertificateLoadResult` + `LoadedClientCertificate` |
| `src/Infrastructure/ExternalSystems/Certificates/FileCertificateProvider.cs` | PFX from mounted file, polled every 2 minutes |
| `src/Infrastructure/ExternalSystems/Http/ExternalSystemHandlerFactory.cs` | Primary handlers: mTLS, server trust, fail-fast |
| `src/Infrastructure/ExternalSystems/Http/FailFastHandler.cs` | Throws `HttpRequestException` when a system cannot be called |
| `src/Infrastructure/ExternalSystems/Http/OutboundTrafficHandler.cs` | Records `Outbound` / `OutboundAttempt` |
| `src/Infrastructure/ExternalSystems/Auth/PrivateKeyJwtAssertionService.cs` | Duende `IClientAssertionService` |
| `src/Infrastructure/ExternalSystems/Health/ExternalSystemHealth.cs` | Tag constant and the readiness predicate |
| `src/Infrastructure/ExternalSystems/Health/ProbeHealthCheck.cs` | Probe with certificate, no token |
| `src/Infrastructure/ExternalSystems/Health/CertificateHealthCheck.cs` | Days to `NotAfter` |
| `src/Infrastructure/ExternalSystems/Health/TokenHealthCheck.cs` | Can a token be obtained |
| `src/Application/Abstractions/Traffic.cs` | + `Outbound`, `OutboundAttempt` |
| `src/Infrastructure/Monitoring/TrafficReader.cs` | Inbound kinds only |
| `src/Api/Program.cs`, `src/Worker/Program.cs` | Call `AddExternalSystems`; readiness predicate |
| `scripts/new-dev-certs.ps1`, `.gitignore` | Dev certificates in `.certs/` |
| `docs/adr/0031-outbound-integrations-with-external-systems.md` | The decision |
| `.claude/skills/external-systems/SKILL.md`, `CLAUDE.md`, `src/Infrastructure/CLAUDE.md` | How to add a partner |

---

### Task 0: Branch

- [ ] **Step 1: Create the branch from an up-to-date `main`**

```bash
git switch main && git pull --ff-only && git switch -c claude/external-systems
```

PR 1 (the spec and this plan, `docs(integrations): design for outbound external systems`) is opened separately from `claude/external-systems-design` before this task; this branch starts after it merges, or is rebased onto it.

---

### Task 1: Test PKI and the partner simulator

**Files:**
- Create: `tests/PartnerSimulator/AiFramework.PartnerSimulator.csproj`
- Create: `tests/PartnerSimulator/TestPki.cs`
- Create: `tests/PartnerSimulator/PartnerSimulatorApp.cs`
- Create: `tests/PartnerSimulator/Program.cs`
- Modify: `AiFramework.slnx` (add the project under `/tests/`)
- Modify: `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj` (reference it)
- Test: `tests/Infrastructure.Tests/ExternalSystems/PartnerSimulatorTests.cs`

**Interfaces:**
- Produces:
  - `TestPki.Create(string name = "AiFramework Test") : TestPki` with `Root`, `Intermediate` (`X509Certificate2`), `IssueClient(string commonName, DateTimeOffset? notAfter = null) : X509Certificate2`, `IssueServer() : X509Certificate2`, `ExportPfx(X509Certificate2 leaf, string? password) : byte[]` (leaf + intermediate), `RootPem : string`, `static Usable(X509Certificate2) : X509Certificate2`, `WriteDevFiles(string directory)`.
  - `PartnerSimulatorApp.StartAsync(PartnerSimulatorOptions, CancellationToken) : Task<PartnerSimulatorApp>` with `BaseAddress : Uri`, `EnqueueEchoStatus(HttpStatusCode)`, `EchoRequests : IReadOnlyList<SimulatorRequest>`, `PingRequests : int`, `DisposeAsync()`.
  - `PartnerSimulatorOptions { required X509Certificate2 ServerCertificate; required X509Certificate2 TrustedClientRoot; string? JwtAuthority }`.
  - `SimulatorRequest(string? Authorization, string? ClientCertificateSubject)`; `/echo` returns `EchoResponse(string? ClientCertificateSubject, string? Subject)` as JSON.

- [ ] **Step 1: Create the project**

`tests/PartnerSimulator/AiFramework.PartnerSimulator.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <!-- Validates the Keycloak-issued bearer on /echo. Same 10.0.x train as the rest of the repo. -->
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.12" />
  </ItemGroup>

</Project>
```

Add to `AiFramework.slnx` after the Infrastructure.Tests folder:

```xml
  <Folder Name="/tests/PartnerSimulator/">
    <Project Path="tests/PartnerSimulator/AiFramework.PartnerSimulator.csproj" />
  </Folder>
```

Add to `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj`'s `ProjectReference` group:

```xml
    <ProjectReference Include="..\PartnerSimulator\AiFramework.PartnerSimulator.csproj" />
```

- [ ] **Step 2: Write `TestPki`**

`tests/PartnerSimulator/TestPki.cs`:

```csharp
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
        const string password = "dev-only";

        File.WriteAllText(Path.Combine(directory, "ca.pem"), RootPem);
        File.WriteAllBytes(Path.Combine(directory, "client.pfx"), ExportPfx(IssueClient("aiframework-dev-client"), password));
        File.WriteAllText(Path.Combine(directory, "client.pass"), password);
        File.WriteAllBytes(Path.Combine(directory, "server.pfx"), ExportPfx(IssueServer(), password));
        File.WriteAllText(Path.Combine(directory, "server.pass"), password);
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
```

- [ ] **Step 3: Write the simulator**

`tests/PartnerSimulator/PartnerSimulatorApp.cs`:

```csharp
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
}

public sealed record SimulatorRequest(string? Authorization, string? ClientCertificateSubject);

public sealed record EchoResponse(string? ClientCertificateSubject, string? Subject);

/// <summary>
/// An mTLS partner on a random loopback port, in-process, with real TLS — TestServer would skip
/// the handshake that is half of what the tests are about. Tests script /echo's next statuses and
/// read back what it received.
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

    public static async Task<PartnerSimulatorApp> StartAsync(
        PartnerSimulatorOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
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
            })));

        if (options.JwtAuthority is not null)
        {
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(jwt =>
                {
                    jwt.Authority = options.JwtAuthority;
                    jwt.RequireHttpsMetadata = false;
                    // Keycloak's client-credentials tokens carry aud "account" unless a mapper is
                    // added; the simulator checks issuer, signature and lifetime only.
                    jwt.TokenValidationParameters.ValidateAudience = false;
                });
            builder.Services.AddAuthorization();
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

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var address = app.Services.GetRequiredService<IServer>().Features
            .GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        simulator.BaseAddress = new Uri(address.EndsWith('/') ? address : address + "/");
        return simulator;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync().ConfigureAwait(false);
}
```

`tests/PartnerSimulator/Program.cs`:

```csharp
using AiFramework.PartnerSimulator;

// Only one command in PR 2: generate the dev PKI for scripts/new-dev-certs.ps1. Serving as a
// standalone container for the dev loop and e2e arrives with PR 3.
if (args is ["generate-certs", var directory])
{
    using var pki = TestPki.Create("AiFramework Dev");
    pki.WriteDevFiles(directory);
    Console.WriteLine($"Wrote ca.pem, client.pfx/.pass and server.pfx/.pass to {Path.GetFullPath(directory)}");
    return 0;
}

Console.Error.WriteLine("Usage: dotnet run --project tests/PartnerSimulator -- generate-certs <directory>");
return 2;
```

- [ ] **Step 4: Write the failing tests**

`tests/Infrastructure.Tests/ExternalSystems/PartnerSimulatorTests.cs`:

```csharp
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using AiFramework.PartnerSimulator;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

/// <summary>
/// Proves the simulator itself, with a hand-built client, before anything in src/ relies on it:
/// later tests read "rejected" as our handler's fault, which is only true if the simulator
/// accepts a correct client and rejects a wrong one.
/// </summary>
public sealed class PartnerSimulatorTests
{
    [Fact]
    public async Task Ping_WithAClientCertificateFromTheTrustedPki_Returns200()
    {
        using var pki = TestPki.Create();
        await using var simulator = await StartAsync(pki);
        using var client = ClientPresenting(pki, TestPki.Usable(pki.IssueClient("client")));

        var response = await client.GetAsync(new Uri(simulator.BaseAddress, "ping"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ping_WithoutAClientCertificate_FailsTheHandshake()
    {
        using var pki = TestPki.Create();
        await using var simulator = await StartAsync(pki);
        using var client = ClientPresenting(pki, certificate: null);

        var act = () => client.GetAsync(new Uri(simulator.BaseAddress, "ping"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    internal static async Task<PartnerSimulatorApp> StartAsync(TestPki pki, string? jwtAuthority = null) =>
        await PartnerSimulatorApp.StartAsync(
            new PartnerSimulatorOptions
            {
                ServerCertificate = TestPki.Usable(pki.IssueServer()),
                TrustedClientRoot = pki.Root,
                JwtAuthority = jwtAuthority,
            },
            CancellationToken.None);

    private static HttpClient ClientPresenting(TestPki pki, X509Certificate2? certificate)
    {
        var ssl = new SslClientAuthenticationOptions();
        if (certificate is not null)
        {
            ssl.ClientCertificateContext = SslStreamCertificateContext.Create(
                certificate, [X509CertificateLoader.LoadCertificate(pki.Intermediate.RawData)], offline: true);
        }

        var trust = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        trust.CustomTrustStore.Add(pki.Root);
        ssl.CertificateChainPolicy = trust;

        return new HttpClient(new SocketsHttpHandler { SslOptions = ssl });
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~PartnerSimulatorTests"`
Expected: both PASS. (They test the simulator, so there is no red phase for production code here; if the first fails, the simulator's TLS setup is wrong — fix it before continuing, every later task depends on it.)

- [ ] **Step 6: Verify the generate-certs command**

Run: `dotnet run --project tests/PartnerSimulator -- generate-certs "$CLAUDE_JOB_DIR/tmp/certs-check"` (any scratch directory)
Expected: prints the five file names; `ls` shows `ca.pem client.pass client.pfx server.pass server.pfx`. Delete the directory afterwards.

- [ ] **Step 7: Commit**

```bash
git add tests/PartnerSimulator tests/Infrastructure.Tests AiFramework.slnx
git commit -m "feat(integrations): add a throwaway PKI and an in-process mTLS partner simulator"
```

---

### Task 2: Options, validation, and the registration entry point

**Files:**
- Create: `src/Infrastructure/ExternalSystems/ExternalSystemsOptions.cs`
- Create: `src/Infrastructure/ExternalSystems/ExternalSystemsOptionsValidator.cs`
- Create: `src/Infrastructure/ExternalSystems/ExternalSystemNames.cs`
- Create: `src/Infrastructure/ExternalSystems/ExternalSystemsRegistration.cs`
- Create: `src/Infrastructure/ExternalSystems/ExternalSystemsBuilder.cs`
- Test: `tests/Infrastructure.Tests/ExternalSystems/ExternalSystemsOptionsValidatorTests.cs`
- Test: `tests/Infrastructure.Tests/ExternalSystems/ExternalSystemsTestConfiguration.cs` (shared helper)

**Interfaces:**
- Produces:
  - `ExternalSystemsOptions { const string SectionName = "ExternalSystems"; IDictionary<string, ExternalSystemOptions> Systems { get; } ; ExternalSystemOptions? Find(string name) }`
  - `ExternalSystemOptions { string BaseAddress; ProbeOptions Probe; ExternalSystemResilienceOptions Resilience; ExternalSystemAuthOptions Auth; ClientCertificateOptions? ClientCertificate; ServerTrustOptions? ServerTrust; TimeSpan CertificateExpiryWarning }`
  - `ExternalSystemAuthKind { None, ClientSecret, PrivateKeyJwt }`, `ExternalSystemCredentialStyle { AuthorizationHeader, PostBody }`
  - `ExternalSystemNames.Probe(name)`, `.TokenBackchannel(name)`, `.CertificateCheck(name)`, `.TokenCheck(name)` — all `string`.
  - `ExternalSystemsRegistration.AddExternalSystems(this IServiceCollection, IConfiguration section) : ExternalSystemsBuilder`
  - `ExternalSystemsBuilder { IServiceCollection Services; ExternalSystemsOptions Snapshot }` — `Snapshot` is the registration-time binding later tasks read to decide what to register.
  - `ExternalSystemsTestConfiguration.Section(IDictionary<string, string?> values) : IConfigurationSection` for tests.

- [ ] **Step 1: Write the failing validator tests**

`tests/Infrastructure.Tests/ExternalSystems/ExternalSystemsTestConfiguration.cs`:

```csharp
using Microsoft.Extensions.Configuration;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

internal static class ExternalSystemsTestConfiguration
{
    /// <summary>An "ExternalSystems" section built from flat keys relative to it, e.g. "Systems:Sim:BaseAddress".</summary>
    public static IConfigurationSection Section(IDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                pair => $"ExternalSystems:{pair.Key}", pair => pair.Value, StringComparer.Ordinal))
            .Build()
            .GetSection("ExternalSystems");
}
```

`tests/Infrastructure.Tests/ExternalSystems/ExternalSystemsOptionsValidatorTests.cs`:

```csharp
using AiFramework.Infrastructure.ExternalSystems;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class ExternalSystemsOptionsValidatorTests
{
    private static ExternalSystemsOptions With(Action<ExternalSystemOptions> configure)
    {
        var system = new ExternalSystemOptions { BaseAddress = "https://partner.example/api/" };
        configure(system);
        var options = new ExternalSystemsOptions();
        options.Systems["Partner"] = system;
        return options;
    }

    private static string Failures(ExternalSystemsOptions options) =>
        string.Join(" | ", new ExternalSystemsOptionsValidator().Validate(null, options).Failures ?? []);

    [Fact]
    public void Validate_AMinimalSystem_Succeeds()
    {
        var result = new ExternalSystemsOptionsValidator().Validate(null, With(_ => { }));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_NoSystems_Succeeds()
    {
        var result = new ExternalSystemsOptionsValidator().Validate(null, new ExternalSystemsOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("ftp://partner.example/")]
    public void Validate_ABaseAddressThatIsNotAbsoluteHttp_Fails(string baseAddress)
    {
        var failures = Failures(With(s => s.BaseAddress = baseAddress));

        failures.Should().Contain("ExternalSystems:Systems:Partner:BaseAddress");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("colon:name")]
    [InlineData("")]
    public void Validate_ASystemNameOutsideLettersDigitsAndDashes_Fails(string name)
    {
        var options = new ExternalSystemsOptions();
        options.Systems[name] = new ExternalSystemOptions { BaseAddress = "https://partner.example/" };

        var failures = Failures(options);

        failures.Should().Contain("name");
    }

    [Fact]
    public void Validate_AnAttemptTimeoutLongerThanTheTotal_Fails()
    {
        var failures = Failures(With(s =>
        {
            s.Resilience.TotalRequestTimeout = TimeSpan.FromSeconds(5);
            s.Resilience.AttemptTimeout = TimeSpan.FromSeconds(6);
        }));

        failures.Should().Contain("AttemptTimeout");
    }

    [Fact]
    public void Validate_ZeroRetryAttempts_FailsAndPointsAtWithoutRetry()
    {
        var failures = Failures(With(s => s.Resilience.MaxRetryAttempts = 0));

        failures.Should().Contain("WithoutRetry");
    }

    [Fact]
    public void Validate_ClientSecretWithoutASecretFile_Fails()
    {
        var failures = Failures(With(s =>
        {
            s.Auth.Kind = ExternalSystemAuthKind.ClientSecret;
            s.Auth.TokenEndpoint = "https://idp.example/token";
            s.Auth.ClientId = "client";
        }));

        failures.Should().Contain("ClientSecretFile");
    }

    [Fact]
    public void Validate_PrivateKeyJwtWithoutACertificate_Fails()
    {
        var failures = Failures(With(s =>
        {
            s.Auth.Kind = ExternalSystemAuthKind.PrivateKeyJwt;
            s.Auth.TokenEndpoint = "https://idp.example/token";
            s.Auth.Issuer = "https://idp.example/";
            s.Auth.ClientId = "client";
        }));

        failures.Should().Contain("ClientCertificate");
    }

    [Fact]
    public void Validate_PrivateKeyJwtWithoutAnIssuer_Fails()
    {
        var failures = Failures(With(s =>
        {
            s.Auth.Kind = ExternalSystemAuthKind.PrivateKeyJwt;
            s.Auth.TokenEndpoint = "https://idp.example/token";
            s.Auth.ClientId = "client";
            s.ClientCertificate = new ClientCertificateOptions { Path = "client.pfx" };
        }));

        failures.Should().Contain("Issuer");
    }

    [Fact]
    public void Validate_AuthWithoutAClientId_Fails()
    {
        var failures = Failures(With(s =>
        {
            s.Auth.Kind = ExternalSystemAuthKind.ClientSecret;
            s.Auth.TokenEndpoint = "https://idp.example/token";
            s.Auth.ClientSecretFile = "secret";
        }));

        failures.Should().Contain("ClientId");
    }

    [Fact]
    public void Validate_AnEmptyServerTrustPath_Fails()
    {
        var failures = Failures(With(s => s.ServerTrust = new ServerTrustOptions { CaBundlePath = "" }));

        failures.Should().Contain("CaBundlePath");
    }

    [Fact]
    public void Bind_FromConfiguration_ReadsNestedOptionsAndLeavesAbsentOnesNull()
    {
        var section = ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>
        {
            ["Systems:Sim:BaseAddress"] = "https://localhost:55690/",
            ["Systems:Sim:Auth:Kind"] = "ClientSecret",
            ["Systems:Sim:Resilience:MaxRetryAttempts"] = "4",
        });

        var options = section.Get<ExternalSystemsOptions>()!; // Section() always has a Systems child here.

        options.Find("sim").Should().NotBeNull("system names are case-insensitive, like every config key");
        options.Find("Sim")!.Auth.Kind.Should().Be(ExternalSystemAuthKind.ClientSecret);
        options.Find("Sim")!.Resilience.MaxRetryAttempts.Should().Be(4);
        options.Find("Sim")!.ClientCertificate.Should().BeNull();
    }
}
```

Add `using Microsoft.Extensions.Configuration;` to the test file for `Get<T>()`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ExternalSystemsOptionsValidatorTests"`
Expected: build FAILS — `ExternalSystemsOptions` does not exist.

- [ ] **Step 3: Write the options**

`src/Infrastructure/ExternalSystems/ExternalSystemsOptions.cs`:

```csharp
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
```

- [ ] **Step 4: Write the validator**

`src/Infrastructure/ExternalSystems/ExternalSystemsOptionsValidator.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// Shape only, at startup. Files are NOT checked here: a missing certificate is one system's
/// runtime health failure, never a host that refuses to start (spec §2).
/// </summary>
internal sealed class ExternalSystemsOptionsValidator : IValidateOptions<ExternalSystemsOptions>
{
    public ValidateOptionsResult Validate(string? name, ExternalSystemsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        foreach (var (systemName, system) in options.Systems)
        {
            Check(systemName, system, failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(string name, ExternalSystemOptions system, List<string> failures)
    {
        var at = $"ExternalSystems:Systems:{name}";

        // The name ends up in health-check names, metric labels and a traffic row's Name, and
        // ":" is the separator in the first of those.
        if (name.Length is 0 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            failures.Add($"{at}: the system name must be 1-64 ASCII letters, digits or dashes.");
        }

        if (!IsAbsoluteHttp(system.BaseAddress))
        {
            failures.Add($"{at}:BaseAddress must be an absolute http or https URI.");
        }

        if (system.Probe.Method is not ("GET" or "HEAD"))
        {
            failures.Add($"{at}:Probe:Method must be GET or HEAD.");
        }

        if (system.Probe.Timeout <= TimeSpan.Zero)
        {
            failures.Add($"{at}:Probe:Timeout must be positive.");
        }

        CheckResilience(at, system.Resilience, failures);
        CheckAuth(at, system, failures);

        if (system.ClientCertificate is { Path.Length: 0 })
        {
            failures.Add($"{at}:ClientCertificate:Path is required when ClientCertificate is configured.");
        }

        if (system.ServerTrust is { CaBundlePath.Length: 0 })
        {
            failures.Add($"{at}:ServerTrust:CaBundlePath is required when ServerTrust is configured.");
        }

        if (system.CertificateExpiryWarning <= TimeSpan.Zero)
        {
            failures.Add($"{at}:CertificateExpiryWarning must be positive.");
        }
    }

    private static void CheckResilience(string at, ExternalSystemResilienceOptions resilience, List<string> failures)
    {
        if (resilience.TotalRequestTimeout <= TimeSpan.Zero || resilience.AttemptTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{at}:Resilience timeouts must be positive.");
        }

        // The standard handler would otherwise throw while BUILDING the pipeline, naming neither
        // property — the green-build, dead-host shape ADR 0014 records.
        if (resilience.AttemptTimeout > resilience.TotalRequestTimeout)
        {
            failures.Add($"{at}:Resilience:AttemptTimeout must not exceed TotalRequestTimeout.");
        }

        if (resilience.MaxRetryAttempts < 1)
        {
            failures.Add(
                $"{at}:Resilience:MaxRetryAttempts must be at least 1: Polly rejects zero. A client " +
                "that must not retry is registered with WithoutRetry(reason).");
        }

        if (resilience.BaseDelay <= TimeSpan.Zero)
        {
            failures.Add($"{at}:Resilience:BaseDelay must be positive.");
        }
    }

    private static void CheckAuth(string at, ExternalSystemOptions system, List<string> failures)
    {
        var auth = system.Auth;
        if (auth.Kind == ExternalSystemAuthKind.None)
        {
            return;
        }

        if (!IsAbsoluteHttp(auth.TokenEndpoint))
        {
            failures.Add($"{at}:Auth:TokenEndpoint must be an absolute http or https URI.");
        }

        if (string.IsNullOrWhiteSpace(auth.ClientId))
        {
            failures.Add($"{at}:Auth:ClientId is required.");
        }

        if (auth.Kind == ExternalSystemAuthKind.ClientSecret && string.IsNullOrWhiteSpace(auth.ClientSecretFile))
        {
            failures.Add($"{at}:Auth:ClientSecretFile is required for ClientSecret.");
        }

        if (auth.Kind == ExternalSystemAuthKind.PrivateKeyJwt)
        {
            if (system.ClientCertificate is null)
            {
                failures.Add($"{at}:ClientCertificate is required for PrivateKeyJwt: it signs the assertion.");
            }

            if (!IsAbsoluteHttp(auth.Issuer))
            {
                failures.Add($"{at}:Auth:Issuer must be the authorization server's absolute issuer URL for PrivateKeyJwt.");
            }
        }
    }

    // Scheme, not merely "absolute": on Linux "/rates" parses as an absolute file:// URI.
    // ResilienceRegistration records the same trap.
    private static bool IsAbsoluteHttp(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal));
}
```

- [ ] **Step 5: Write the names, the builder and the registration entry point**

`src/Infrastructure/ExternalSystems/ExternalSystemNames.cs`:

```csharp
namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// The one place a system name is turned into a client or check name. Composing these anywhere
/// else lets the two sides drift with no error — the CacheScope lesson.
/// </summary>
internal static class ExternalSystemNames
{
    public static string Probe(string system) => $"external:{system}:probe";

    public static string TokenBackchannel(string system) => $"external:{system}:token-endpoint";

    public static string ProbeCheck(string system) => system;

    public static string CertificateCheck(string system) => $"{system}:certificate";

    public static string TokenCheck(string system) => $"{system}:token";
}
```

`src/Infrastructure/ExternalSystems/ExternalSystemsBuilder.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// Returned by AddExternalSystems. Carries the registration-time binding, because what to
/// register — a token handler, a certificate check — depends on configuration that has to be
/// known before the container exists (the same reason the worker reads Jobs off configuration).
/// </summary>
public sealed class ExternalSystemsBuilder
{
    internal ExternalSystemsBuilder(IServiceCollection services, ExternalSystemsOptions snapshot)
    {
        Services = services;
        Snapshot = snapshot;
    }

    public IServiceCollection Services { get; }

    internal ExternalSystemsOptions Snapshot { get; }
}
```

`src/Infrastructure/ExternalSystems/ExternalSystemsRegistration.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.ExternalSystems;

public static class ExternalSystemsRegistration
{
    /// <summary>
    /// Every configured external system: options, validation, and — added by later tasks — the
    /// certificate provider, primary handlers, probe clients, health checks and token clients.
    /// Called by EACH host's Program.cs, not by AddInfrastructure, because the set of named
    /// clients must be known at registration time. ADR 0031.
    /// </summary>
    public static ExternalSystemsBuilder AddExternalSystems(
        this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        services.AddOptions<ExternalSystemsOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ExternalSystemsOptions>, ExternalSystemsOptionsValidator>();

        var snapshot = section.Get<ExternalSystemsOptions>() ?? new ExternalSystemsOptions();
        return new ExternalSystemsBuilder(services, snapshot);
    }
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ExternalSystemsOptionsValidatorTests"`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Infrastructure/ExternalSystems tests/Infrastructure.Tests/ExternalSystems
git commit -m "feat(integrations): configure external systems by name, validated at startup"
```

---

### Task 3: The certificate provider

**Files:**
- Create: `src/Infrastructure/ExternalSystems/Certificates/ICertificateProvider.cs`
- Create: `src/Infrastructure/ExternalSystems/Certificates/FileCertificateProvider.cs`
- Modify: `src/Infrastructure/ExternalSystems/ExternalSystemsRegistration.cs` (register it)
- Test: `tests/Infrastructure.Tests/ExternalSystems/FileCertificateProviderTests.cs`

**Interfaces:**
- Consumes: `ExternalSystemsOptions.Find`, `TestPki`.
- Produces:
  - `ICertificateProvider.GetCurrent(string systemName) : CertificateLoadResult`
  - `CertificateLoadResult(LoadedClientCertificate? Certificate, string? Problem)` with `static Loaded(LoadedClientCertificate)`, `static Failed(string)`, `static NotConfigured`
  - `LoadedClientCertificate { X509Certificate2 Certificate; SslStreamCertificateContext Context; DateTimeOffset NotAfter }`
  - `FileCertificateProvider.RecheckInterval = TimeSpan.FromMinutes(2)`

- [ ] **Step 1: Write the failing tests**

`tests/Infrastructure.Tests/ExternalSystems/FileCertificateProviderTests.cs`:

```csharp
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.ExternalSystems.Certificates;
using AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class FileCertificateProviderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-certs-").FullName;
    private readonly TestPki _pki = TestPki.Create();
    private readonly FakeTimeProvider _clock = new();

    public void Dispose()
    {
        _pki.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private FileCertificateProvider Provider(string? passwordFile = "client.pass")
    {
        var options = new ExternalSystemsOptions();
        options.Systems["Sim"] = new ExternalSystemOptions
        {
            BaseAddress = "https://localhost/",
            ClientCertificate = new ClientCertificateOptions
            {
                Path = Path.Combine(_directory, "client.pfx"),
                PasswordFile = passwordFile is null ? null : Path.Combine(_directory, passwordFile),
            },
        };
        return new FileCertificateProvider(
            new StaticOptionsMonitor(options), _clock, NullLogger<FileCertificateProvider>.Instance);
    }

    private void WritePfx(string commonName, string password = "secret")
    {
        File.WriteAllBytes(Path.Combine(_directory, "client.pfx"), _pki.ExportPfx(_pki.IssueClient(commonName), password));
        File.WriteAllText(Path.Combine(_directory, "client.pass"), password + "\n");
    }

    [Fact]
    public void GetCurrent_WithAValidPfx_LoadsTheLeafAndItsIntermediate()
    {
        WritePfx("first");

        var result = Provider().GetCurrent("Sim");

        result.Problem.Should().BeNull();
        result.Certificate!.Certificate.Subject.Should().Be("CN=first"); // Problem is null, so Certificate is set.
        result.Certificate.Context.IntermediateCertificates.Should().ContainSingle(
            c => c.Thumbprint == _pki.Intermediate.Thumbprint);
    }

    [Fact]
    public void GetCurrent_WhenTheFileIsMissing_ReportsAProblem()
    {
        var result = Provider().GetCurrent("Sim");

        result.Certificate.Should().BeNull();
        result.Problem.Should().Be("client certificate file not found");
    }

    [Fact]
    public void GetCurrent_WithAWrongPassword_ReportsAProblemWithoutThePath()
    {
        WritePfx("first");
        File.WriteAllText(Path.Combine(_directory, "client.pass"), "wrong");

        var result = Provider().GetCurrent("Sim");

        result.Certificate.Should().BeNull();
        result.Problem.Should().StartWith("client certificate could not be read").And.NotContain(_directory);
    }

    [Fact]
    public void GetCurrent_ForASystemWithoutACertificate_IsNotConfigured()
    {
        var result = Provider().GetCurrent("Unknown");

        result.Should().Be(CertificateLoadResult.NotConfigured);
    }

    [Fact]
    public void GetCurrent_AfterTheFileChangesWithinTheRecheckInterval_KeepsTheOldCertificate()
    {
        WritePfx("first");
        var provider = Provider();
        provider.GetCurrent("Sim");
        WritePfx("second");
        _clock.Advance(FileCertificateProvider.RecheckInterval - TimeSpan.FromSeconds(1));

        var result = provider.GetCurrent("Sim");

        result.Certificate!.Certificate.Subject.Should().Be("CN=first"); // loaded above.
    }

    [Fact]
    public void GetCurrent_AfterTheFileChangesAndTheRecheckIntervalPasses_LoadsTheNewCertificate()
    {
        WritePfx("first");
        var provider = Provider();
        provider.GetCurrent("Sim");
        WritePfx("second");
        _clock.Advance(FileCertificateProvider.RecheckInterval);

        var result = provider.GetCurrent("Sim");

        result.Certificate!.Certificate.Subject.Should().Be("CN=second"); // the new file is valid.
    }

    private sealed class StaticOptionsMonitor(ExternalSystemsOptions value) : IOptionsMonitor<ExternalSystemsOptions>
    {
        public ExternalSystemsOptions CurrentValue => value;

        public ExternalSystemsOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ExternalSystemsOptions, string?> listener) => null;
    }
}
```

Move `StaticOptionsMonitor` into its own file `tests/Infrastructure.Tests/ExternalSystems/StaticOptionsMonitor.cs` (as `internal sealed class StaticOptionsMonitor`) — Tasks 4, 5 and 7 use it too.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~FileCertificateProviderTests"`
Expected: build FAILS — `FileCertificateProvider` does not exist.

- [ ] **Step 3: Write the port**

`src/Infrastructure/ExternalSystems/Certificates/ICertificateProvider.cs`:

```csharp
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
```

- [ ] **Step 4: Write the provider**

`src/Infrastructure/ExternalSystems/Certificates/FileCertificateProvider.cs`:

```csharp
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
        catch (FileNotFoundException)
        {
            return Failed(systemName, "client certificate file not found");
        }
        catch (DirectoryNotFoundException)
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
        var loaded = new LoadedClientCertificate(
            leaf, SslStreamCertificateContext.Create(leaf, intermediates, offline: true));

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
```

If the analyzers reject the `catch` ladder (Sonar can flag duplicate catch bodies), merge the two `FileNotFound`/`DirectoryNotFound` blocks with an exception filter: `catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)` placed before the general `IOException` block.

- [ ] **Step 5: Register it**

In `ExternalSystemsRegistration.AddExternalSystems`, before `var snapshot = …`:

```csharp
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICertificateProvider, FileCertificateProvider>();
```

with `using AiFramework.Infrastructure.ExternalSystems.Certificates;` and `using Microsoft.Extensions.DependencyInjection.Extensions;`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~FileCertificateProviderTests"`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Infrastructure/ExternalSystems tests/Infrastructure.Tests/ExternalSystems
git commit -m "feat(integrations): load client certificates from mounted files and pick up rotations"
```

---

### Task 4: Primary handlers — mTLS, server trust, fail fast

**Files:**
- Create: `src/Infrastructure/ExternalSystems/Http/ExternalSystemHandlerFactory.cs`
- Create: `src/Infrastructure/ExternalSystems/Http/FailFastHandler.cs`
- Modify: `src/Infrastructure/ExternalSystems/ExternalSystemsRegistration.cs`
- Test: `tests/Infrastructure.Tests/ExternalSystems/ExternalSystemHandlerFactoryTests.cs`
- Test: `tests/Infrastructure.Tests/ExternalSystems/NoAcceptAnyCertificateTests.cs`

**Interfaces:**
- Consumes: `ICertificateProvider`, `ExternalSystemsOptions`, `TestPki`, `PartnerSimulatorApp`, `StaticOptionsMonitor`.
- Produces: `ExternalSystemHandlerFactory.CreatePrimaryHandler(string systemName) : HttpMessageHandler` (used by every client), `ExternalSystemHandlerFactory.CreateTokenEndpointHandler(string systemName) : HttpMessageHandler` (same certificate, OS trust for the IdP); `FailFastHandler(string systemName, string problem)` throwing `HttpRequestException` with `HttpRequestError.SecureConnectionError`.

- [ ] **Step 1: Write the failing tests**

`tests/Infrastructure.Tests/ExternalSystems/ExternalSystemHandlerFactoryTests.cs`:

```csharp
using System.Net;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.ExternalSystems.Certificates;
using AiFramework.Infrastructure.ExternalSystems.Http;
using AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

/// <summary>
/// Real TLS against the simulator. The simulator trusts only the ROOT, and every client leaf is
/// issued by the INTERMEDIATE — so a success here means the handler sent the chain. On Windows the
/// machine store can paper over a missing intermediate; CI's Linux run is the real evidence.
/// </summary>
public sealed class ExternalSystemHandlerFactoryTests : IAsyncLifetime, IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-mtls-").FullName;
    private readonly TestPki _pki = TestPki.Create();
    private PartnerSimulatorApp _simulator = null!; // set in InitializeAsync, before any test runs.

    public async Task InitializeAsync() => _simulator = await PartnerSimulatorTests.StartAsync(_pki);

    public async Task DisposeAsync() => await _simulator.DisposeAsync();

    public void Dispose()
    {
        _pki.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private ExternalSystemHandlerFactory Factory(Action<ExternalSystemOptions> configure)
    {
        File.WriteAllText(Path.Combine(_directory, "ca.pem"), _pki.RootPem);
        var system = new ExternalSystemOptions
        {
            BaseAddress = _simulator.BaseAddress.ToString(),
            ServerTrust = new ServerTrustOptions
            {
                CaBundlePath = Path.Combine(_directory, "ca.pem"),
                CheckRevocation = false,
            },
        };
        configure(system);
        var options = new ExternalSystemsOptions();
        options.Systems["Sim"] = system;
        var monitor = new StaticOptionsMonitor(options);
        return new ExternalSystemHandlerFactory(
            new FileCertificateProvider(monitor, TimeProvider.System, NullLogger<FileCertificateProvider>.Instance),
            monitor,
            TimeProvider.System);
    }

    private ClientCertificateOptions WriteClientPfx(TestPki issuer, DateTimeOffset? notAfter = null)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, issuer.ExportPfx(issuer.IssueClient("aiframework", notAfter), password: null));
        return new ClientCertificateOptions { Path = path };
    }

    private async Task<HttpResponseMessage> PingAsync(ExternalSystemHandlerFactory factory)
    {
        using var client = new HttpClient(factory.CreatePrimaryHandler("Sim"));
        return await client.GetAsync(new Uri(_simulator.BaseAddress, "ping"));
    }

    [Fact]
    public async Task Send_WithALeafIssuedByTheIntermediate_Succeeds()
    {
        var factory = Factory(s => s.ClientCertificate = WriteClientPfx(_pki));

        var response = await PingAsync(factory);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Send_WithoutAClientCertificate_FailsTheHandshake()
    {
        var factory = Factory(_ => { });

        var act = () => PingAsync(factory);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Send_WithACertificateFromAnotherPki_FailsTheHandshake()
    {
        using var stranger = TestPki.Create("Stranger");
        var factory = Factory(s => s.ClientCertificate = WriteClientPfx(stranger));

        var act = () => PingAsync(factory);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Send_WithoutServerTrustForATestCa_RejectsTheServer()
    {
        var factory = Factory(s =>
        {
            s.ClientCertificate = WriteClientPfx(_pki);
            s.ServerTrust = null;
        });

        var act = () => PingAsync(factory);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Send_WhenTheCertificateFileIsMissing_FailsFastWithoutTheNetwork()
    {
        var factory = Factory(s => s.ClientCertificate = new ClientCertificateOptions
        {
            Path = Path.Combine(_directory, "absent.pfx"),
        });

        var act = () => PingAsync(factory);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.HttpRequestError.Should().Be(HttpRequestError.SecureConnectionError);
        _simulator.PingRequests.Should().Be(0);
    }

    [Fact]
    public async Task Send_WhenTheCertificateHasExpired_FailsFastWithoutTheNetwork()
    {
        var factory = Factory(s => s.ClientCertificate = WriteClientPfx(_pki, DateTimeOffset.UtcNow.AddDays(-1)));

        var act = () => PingAsync(factory);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .WithMessage("*certificate expired*");
        _simulator.PingRequests.Should().Be(0);
    }

    [Fact]
    public async Task Send_ForAnUnconfiguredSystem_FailsFast()
    {
        var factory = Factory(_ => { });
        using var client = new HttpClient(factory.CreatePrimaryHandler("NotConfigured"));

        var act = () => client.GetAsync(new Uri(_simulator.BaseAddress, "ping"));

        (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("*not configured*");
    }
}
```

`tests/Infrastructure.Tests/ExternalSystems/NoAcceptAnyCertificateTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

/// <summary>
/// Server trust in src/ is CertificateChainPolicy, never a callback: a callback is the only way
/// to write "accept anything", so having none makes that impossible rather than discouraged.
/// </summary>
public sealed class NoAcceptAnyCertificateTests
{
    private static readonly string[] Forbidden =
    [
        "ServerCertificateCustomValidationCallback",
        "RemoteCertificateValidationCallback",
        "DangerousAcceptAnyServerCertificateValidator",
    ];

    [Fact]
    public void Source_UnderSrc_HasNoCertificateValidationCallback()
    {
        var offenders = SourceFiles()
            .SelectMany(file => Forbidden
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(RepoRoot(), file)}: {token}"))
            .ToArray();

        string.Join(Environment.NewLine, offenders).Should().BeEmpty();
    }

    [Fact]
    public void Scan_FindsTheSourceTree()
    {
        SourceFiles().Should().Contain(f => f.EndsWith("FileCertificateProvider.cs", StringComparison.Ordinal),
            "an empty scan would make the rule above pass for the wrong reason");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..")); // CallerFilePath is never empty when the compiler fills it.
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ExternalSystemHandlerFactoryTests|FullyQualifiedName~NoAcceptAnyCertificateTests"`
Expected: build FAILS — `ExternalSystemHandlerFactory` does not exist.

- [ ] **Step 3: Write the fail-fast handler**

`src/Infrastructure/ExternalSystems/Http/FailFastHandler.cs`:

```csharp
namespace AiFramework.Infrastructure.ExternalSystems.Http;

/// <summary>
/// The primary handler for a system that cannot be called — no usable certificate, no trust
/// bundle, not configured. Throws the same exception a failed handshake would, so the adapter's
/// existing HttpRequestException → Unavailable mapping covers it, and never touches the network.
/// </summary>
/// <remarks>
/// IHttpClientFactory caches a primary handler for its lifetime (two minutes), so a fixed
/// certificate is picked up on the next rebuild without a restart.
/// </remarks>
internal sealed class FailFastHandler(string systemName, string problem) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            $"External system '{systemName}' cannot be called: {problem}."));
}
```

- [ ] **Step 4: Write the factory**

`src/Infrastructure/ExternalSystems/Http/ExternalSystemHandlerFactory.cs`:

```csharp
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
            catch (CryptographicException)
            {
                return new FailFastHandler(systemName, "the server trust bundle is not valid PEM");
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
```

Note: `FailFastHandler`'s message reads "cannot be called: the client certificate expired on …", which satisfies the test's `*certificate expired*` pattern.

- [ ] **Step 5: Register it**

In `AddExternalSystems`, after the provider:

```csharp
        services.AddSingleton<ExternalSystemHandlerFactory>();
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ExternalSystemHandlerFactoryTests|FullyQualifiedName~NoAcceptAnyCertificateTests"`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Infrastructure/ExternalSystems tests/Infrastructure.Tests/ExternalSystems
git commit -m "feat(integrations): present the client certificate with its chain, trust per system, fail fast"
```

---

### Task 5: The probe and certificate health checks

**Files:**
- Create: `src/Infrastructure/ExternalSystems/Health/ExternalSystemHealth.cs`
- Create: `src/Infrastructure/ExternalSystems/Health/ProbeHealthCheck.cs`
- Create: `src/Infrastructure/ExternalSystems/Health/CertificateHealthCheck.cs`
- Modify: `src/Infrastructure/ExternalSystems/ExternalSystemsRegistration.cs`
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj` (health checks package)
- Test: `tests/Infrastructure.Tests/ExternalSystems/ProbeHealthCheckTests.cs`
- Test: `tests/Infrastructure.Tests/ExternalSystems/CertificateHealthCheckTests.cs`

**Interfaces:**
- Consumes: `ExternalSystemNames`, `ExternalSystemHandlerFactory`, `ICertificateProvider`, `ExternalSystemsBuilder.Snapshot`.
- Produces: `ExternalSystemHealth.Tag = "external"`, `ExternalSystemHealth.IsNotExternal(HealthCheckRegistration) : bool` (public, for both `Program.cs`), `ExternalSystemHealth.CertificateNotAfterKey = "certificateNotAfter"` (data key PR 3's status job reads).

- [ ] **Step 1: Add the package**

`Microsoft.Extensions.Diagnostics.HealthChecks` — `IHealthCheck` and `AddHealthChecks()` outside ASP.NET Core. Add to `src/Infrastructure/AiFramework.Infrastructure.csproj` beside the other `Microsoft.Extensions.*` 10.0.12 pins:

```xml
    <!-- IHealthCheck for the external-system checks (ExternalSystems/Health). Same 10.0.12 train as
         the other Microsoft.Extensions pins, so NU1605 cannot split them. ADR 0031. -->
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="10.0.12" />
```

Run `dotnet build src/Infrastructure`. Expected: builds. If NU1605 appears, align the version to what the error names, as the csproj's existing comments describe.

- [ ] **Step 2: Write the failing tests**

`tests/Infrastructure.Tests/ExternalSystems/ProbeHealthCheckTests.cs`:

```csharp
using System.Net;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.Tests.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class ProbeHealthCheckTests
{
    private static async Task<HealthReportEntry> ProbeAsync(Func<HttpRequestMessage, HttpResponseMessage> respond,
        string timeout = "00:00:05")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>
        {
            ["Systems:Sim:BaseAddress"] = "https://partner.example/api",
            ["Systems:Sim:Probe:Path"] = "ping",
            ["Systems:Sim:Probe:Timeout"] = timeout,
        }));
        services.AddHttpClient(ExternalSystemNames.Probe("Sim"))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(respond));

        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Name == "Sim");
        return report.Entries["Sim"];
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task CheckHealth_WhenTheProbeAnswersBelow500_IsHealthy(HttpStatusCode status)
    {
        var entry = await ProbeAsync(_ => new HttpResponseMessage(status));

        entry.Status.Should().Be(HealthStatus.Healthy);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CheckHealth_WhenTheProbeAnswers5xx_IsUnhealthy(HttpStatusCode status)
    {
        var entry = await ProbeAsync(_ => new HttpResponseMessage(status));

        entry.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealth_ProbesTheConfiguredPathUnderTheBasePath()
    {
        Uri? requested = null;

        await ProbeAsync(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        requested.Should().Be(new Uri("https://partner.example/api/ping"));
    }

    [Fact]
    public async Task CheckHealth_WhenTheConnectionFails_IsUnhealthyAndNamesOnlyTheCategory()
    {
        var entry = await ProbeAsync(_ => throw new HttpRequestException(
            HttpRequestError.ConnectionError, "Connection refused (partner.example:443)"));

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().Contain("ConnectionError").And.NotContain("partner.example");
    }

    [Fact]
    public async Task CheckHealth_WhenTheProbeOutlivesItsTimeout_IsDegraded()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>
        {
            ["Systems:Sim:BaseAddress"] = "https://partner.example/",
            ["Systems:Sim:Probe:Timeout"] = "00:00:00.200",
        }));
        services.AddHttpClient(ExternalSystemNames.Probe("Sim"))
            .ConfigurePrimaryHttpMessageHandler(() => new HangingHandler());
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Name == "Sim");

        report.Entries["Sim"].Status.Should().Be(HealthStatus.Degraded);
    }

    /// <summary>
    /// Never answers until cancelled. Must be ASYNC: a synchronous stub (StubHttpMessageHandler
    /// with a sleep) cannot be interrupted by the probe's CancellationToken, so it would answer
    /// late but successfully and the check would read Healthy.
    /// </summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable: the delay only ends by cancellation");
        }
    }
}
```

`tests/Infrastructure.Tests/ExternalSystems/CertificateHealthCheckTests.cs`:

```csharp
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class CertificateHealthCheckTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-cert-health-").FullName;
    private readonly TestPki _pki = TestPki.Create();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public void Dispose()
    {
        _pki.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string WritePfx(string name, DateTimeOffset notAfter)
    {
        var path = Path.Combine(_directory, $"{name}.pfx");
        File.WriteAllBytes(path, _pki.ExportPfx(_pki.IssueClient(name, notAfter), password: null));
        return path;
    }

    private async Task<HealthReport> CheckAsync(IDictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(config));
        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(c => c.Name.EndsWith(":certificate", StringComparison.Ordinal));
    }

    private static Dictionary<string, string?> System(string name, string? pfx) => new()
    {
        [$"Systems:{name}:BaseAddress"] = "https://partner.example/",
        [$"Systems:{name}:ClientCertificate:Path"] = pfx,
    };

    [Fact]
    public async Task CheckHealth_WithACertificateFarFromExpiry_IsHealthy()
    {
        var report = await CheckAsync(System("Sim", WritePfx("Sim", _clock.GetUtcNow().AddDays(200))));

        report.Entries["Sim:certificate"].Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealth_WithinTheWarningWindow_IsDegraded()
    {
        var report = await CheckAsync(System("Sim", WritePfx("Sim", _clock.GetUtcNow().AddDays(10))));

        report.Entries["Sim:certificate"].Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task CheckHealth_ReportsNotAfterForTheStatusJob()
    {
        var notAfter = _clock.GetUtcNow().AddDays(200);

        var report = await CheckAsync(System("Sim", WritePfx("Sim", notAfter)));

        report.Entries["Sim:certificate"].Data.Should().ContainKey(ExternalSystemHealth.CertificateNotAfterKey);
    }

    [Fact]
    public async Task CheckHealth_WithAMissingFile_IsUnhealthy()
    {
        var report = await CheckAsync(System("Sim", Path.Combine(_directory, "absent.pfx")));

        report.Entries["Sim:certificate"].Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealth_WithTwoSystemsOneExpired_OnlyTheExpiredOneIsUnhealthy()
    {
        var config = System("Good", WritePfx("Good", _clock.GetUtcNow().AddDays(200)));
        foreach (var (key, value) in System("Expired", WritePfx("Expired", _clock.GetUtcNow().AddDays(-1))))
        {
            config[key] = value;
        }

        var report = await CheckAsync(config);

        report.Entries["Good:certificate"].Status.Should().Be(HealthStatus.Healthy);
        report.Entries["Expired:certificate"].Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task AddExternalSystems_ForASystemWithoutACertificate_RegistersNoCertificateCheck()
    {
        var report = await CheckAsync(System("Plain", pfx: null));

        report.Entries.Should().NotContainKey("Plain:certificate");
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ProbeHealthCheckTests|FullyQualifiedName~CertificateHealthCheckTests"`
Expected: FAIL — no checks registered (`KeyNotFoundException` on `report.Entries[...]`) or build failure on `ExternalSystemHealth`.

- [ ] **Step 4: Write `ExternalSystemHealth`**

`src/Infrastructure/ExternalSystems/Health/ExternalSystemHealth.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.ExternalSystems.Health;

public static class ExternalSystemHealth
{
    /// <summary>Every external-system check carries this tag.</summary>
    public const string Tag = "external";

    /// <summary>The certificate check's data key for NotAfter — read by the status job (PR 3).</summary>
    public const string CertificateNotAfterKey = "certificateNotAfter";

    /// <summary>
    /// The readiness predicate both hosts pass to /health/ready. A partner outage must never
    /// take a pod out of rotation; without this, MapHealthChecks runs EVERY registered check.
    /// </summary>
    public static bool IsNotExternal(HealthCheckRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return !registration.Tags.Contains(Tag);
    }
}
```

- [ ] **Step 5: Write the probe check**

`src/Infrastructure/ExternalSystems/Health/ProbeHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.ExternalSystems.Health;

/// <summary>
/// Is the system reachable — DNS, TCP and the mTLS handshake — with the client certificate but
/// WITHOUT a token, so a 401 is the expected healthy answer. Reading adopted from egdw_eghealth's
/// HttpProbeHealthCheck: below 500 Healthy, 5xx Unhealthy, own timeout Degraded. Descriptions
/// carry the status code or the error CATEGORY only — never a body, host name or port.
/// </summary>
internal sealed partial class ProbeHealthCheck(
    IHttpClientFactory clients,
    IOptionsMonitor<ExternalSystemsOptions> options,
    string systemName,
    ILogger<ProbeHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var probe = options.CurrentValue.Find(systemName)?.Probe ?? new ProbeOptions();
        var client = clients.CreateClient(ExternalSystemNames.Probe(systemName));
        using var request = new HttpRequestMessage(new HttpMethod(probe.Method), probe.Path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(probe.Timeout);

        try
        {
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var code = (int)response.StatusCode;

            return code >= 500
                ? HealthCheckResult.Unhealthy($"{systemName} answered {code}")
                : HealthCheckResult.Healthy($"{systemName} reachable ({code})");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded(
                $"{systemName} did not answer within {probe.Timeout.TotalSeconds:0.#} s");
        }
        catch (HttpRequestException exception)
        {
            LogUnreachable(exception, systemName, exception.HttpRequestError);
            return HealthCheckResult.Unhealthy($"{systemName} unreachable: {exception.HttpRequestError}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Probe of {System} failed: {Category}")]
    private partial void LogUnreachable(Exception exception, string system, HttpRequestError category);
}
```

- [ ] **Step 6: Write the certificate check**

`src/Infrastructure/ExternalSystems/Health/CertificateHealthCheck.cs`:

```csharp
using System.Globalization;
using AiFramework.Infrastructure.ExternalSystems.Certificates;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.ExternalSystems.Health;

internal sealed class CertificateHealthCheck(
    ICertificateProvider certificates,
    IOptionsMonitor<ExternalSystemsOptions> options,
    TimeProvider time,
    string systemName) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = certificates.GetCurrent(systemName);
        if (result.Certificate is null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(result.Problem ?? "no client certificate"));
        }

        var notAfter = result.Certificate.NotAfter;
        var data = new Dictionary<string, object> { [ExternalSystemHealth.CertificateNotAfterKey] = notAfter };
        var remaining = notAfter - time.GetUtcNow();
        var warning = options.CurrentValue.Find(systemName)?.CertificateExpiryWarning ?? TimeSpan.FromDays(30);
        var expires = notAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return Task.FromResult(remaining <= TimeSpan.Zero
            ? HealthCheckResult.Unhealthy($"client certificate expired on {expires}", data: data)
            : remaining < warning
                ? HealthCheckResult.Degraded($"client certificate expires in {remaining.Days} days ({expires})", data: data)
                : HealthCheckResult.Healthy($"client certificate valid until {expires}", data: data));
    }
}
```

- [ ] **Step 7: Register the probe client and both checks**

Add to `AddExternalSystems`, after `var snapshot = …` and before `return`:

```csharp
        var health = services.AddHealthChecks();
        foreach (var (name, system) in snapshot.Systems)
        {
            // Certificate, no token, no retry, no traffic: a probe is not an integration call.
            services.AddHttpClient(ExternalSystemNames.Probe(name), client =>
                {
                    client.BaseAddress = new Uri($"{system.BaseAddress.TrimEnd('/')}/");
                    client.Timeout = Timeout.InfiniteTimeSpan; // ProbeHealthCheck owns the timeout.
                })
                .ConfigurePrimaryHttpMessageHandler(sp =>
                    sp.GetRequiredService<ExternalSystemHandlerFactory>().CreatePrimaryHandler(name));

            string[] tags = [ExternalSystemHealth.Tag, name];
            health.Add(new HealthCheckRegistration(
                ExternalSystemNames.ProbeCheck(name),
                sp => ActivatorUtilities.CreateInstance<ProbeHealthCheck>(sp, name),
                failureStatus: null,
                tags));

            if (system.ClientCertificate is not null)
            {
                health.Add(new HealthCheckRegistration(
                    ExternalSystemNames.CertificateCheck(name),
                    sp => ActivatorUtilities.CreateInstance<CertificateHealthCheck>(sp, name),
                    failureStatus: null,
                    tags));
            }
        }
```

`TrimEnd('/') + "/"`: Uri's combining rule drops the base address's last segment without a trailing slash — the same trap `ResilienceRegistration` records. Usings: `AiFramework.Infrastructure.ExternalSystems.Health`, `AiFramework.Infrastructure.ExternalSystems.Http`, `Microsoft.Extensions.Diagnostics.HealthChecks`.

- [ ] **Step 8: Run the tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ExternalSystems"`
Expected: all PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Infrastructure tests/Infrastructure.Tests/ExternalSystems
git commit -m "feat(integrations): probe each external system and check its certificate's expiry"
```

---

### Task 6: Outbound traffic

**Files:**
- Modify: `src/Application/Abstractions/Traffic.cs` (two enum members)
- Create: `src/Infrastructure/ExternalSystems/Http/OutboundTrafficHandler.cs`
- Modify: `src/Infrastructure/Monitoring/TrafficReader.cs` (inbound kinds only)
- Modify (regenerated): `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts`
- Test: `tests/Infrastructure.Tests/ExternalSystems/OutboundTrafficHandlerTests.cs`
- Test: `tests/Infrastructure.Tests/Monitoring/TrafficReaderTests.cs` (create if absent; else add the test)

**Interfaces:**
- Produces: `TrafficKind.Outbound`, `TrafficKind.OutboundAttempt`; `OutboundTrafficHandler(ITrafficRecorder recorder, TimeProvider time, string systemName, TrafficKind kind) : DelegatingHandler`.

- [ ] **Step 1: Write the failing handler tests**

`tests/Infrastructure.Tests/ExternalSystems/OutboundTrafficHandlerTests.cs`:

```csharp
using System.Net;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems.Http;
using AiFramework.Infrastructure.Tests.Resilience;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class OutboundTrafficHandlerTests
{
    private readonly ITrafficRecorder _recorder = Substitute.For<ITrafficRecorder>();

    private async Task SendAsync(HttpMessageHandler inner, CancellationToken cancellationToken = default)
    {
        using var handler = new OutboundTrafficHandler(_recorder, TimeProvider.System, "Sim", TrafficKind.Outbound)
        {
            InnerHandler = inner,
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://partner.example/echo");
        using var response = await invoker.SendAsync(request, cancellationToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, TrafficOutcome.Succeeded)]
    [InlineData(HttpStatusCode.NotFound, TrafficOutcome.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TrafficOutcome.Faulted)]
    public async Task Send_RecordsTheOutcomeOfTheStatus(HttpStatusCode status, TrafficOutcome expected)
    {
        await SendAsync(new StubHttpMessageHandler(_ => new HttpResponseMessage(status)));

        _recorder.Received(1).Record(TrafficKind.Outbound, "Sim", expected, Arg.Any<long>());
    }

    [Fact]
    public async Task Send_WhenTheCallThrows_RecordsAFaultAndRethrows()
    {
        var act = () => SendAsync(new StubHttpMessageHandler(_ => throw new HttpRequestException("refused")));

        await Assert.ThrowsAsync<HttpRequestException>(act);
        _recorder.Received(1).Record(TrafficKind.Outbound, "Sim", TrafficOutcome.Faulted, Arg.Any<long>());
    }

    [Fact]
    public async Task Send_WhenTheCallerCancels_RecordsNothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => SendAsync(
            new StubHttpMessageHandler(_ => throw new OperationCanceledException(cancelled.Token)),
            cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        _recorder.DidNotReceiveWithAnyArgs().Record(default, default!, default, default);
    }
}
```

- [ ] **Step 2: Write the failing reader test**

Check whether `tests/Infrastructure.Tests/Monitoring/TrafficReaderTests.cs` exists (`ls tests/Infrastructure.Tests/Monitoring`). If it does not, create it following `TrafficFlushTests.cs`'s Postgres setup — open that file and copy its class attributes (`[Collection(nameof(PostgresCollection))]`), constructor and the way it creates an `AiFrameworkDbContext` from the fixture. Then add:

```csharp
    [Fact]
    public async Task SummarizeAsync_IgnoresOutboundKinds()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-10);
        var minute = new DateTimeOffset(since.Year, since.Month, since.Day, since.Hour, since.Minute, 0, TimeSpan.Zero)
            .AddMinutes(5);
        await using (var context = CreateContext())
        {
            context.TrafficBuckets.AddRange(
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Http, Name = "GET /api/orders", InstanceId = "pod", Succeeded = 3 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Outbound, Name = "Sim", InstanceId = "pod", Faulted = 7 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.OutboundAttempt, Name = "Sim", InstanceId = "pod", Faulted = 21 });
            await context.SaveChangesAsync();
        }

        await using var reading = CreateContext();
        var summary = await new TrafficReader(reading).SummarizeAsync(since, CancellationToken.None);

        summary.Overall.Total.Should().Be(3);
        summary.Rows.Should().OnlyContain(row => row.Kind == TrafficKind.Http);
    }
```

`CreateContext()` is whatever helper the copied setup uses to build a context against the fixture's database; name it so. If the fixture shares one database across tests, use a unique `InstanceId`/`Name` per test and a `since` window that only this test writes into, or clean the table in the test's own arrange — match what `TrafficFlushTests` does.

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~OutboundTrafficHandlerTests|FullyQualifiedName~TrafficReaderTests"`
Expected: build FAILS — `TrafficKind.Outbound` and `OutboundTrafficHandler` do not exist.

- [ ] **Step 4: Add the kinds**

In `src/Application/Abstractions/Traffic.cs`, append to `TrafficKind`:

```csharp
    /// <summary>
    /// One call to an external system, counted OUTSIDE its retry: its duration includes every
    /// retry, and an open circuit or a timeout is a Faulted call. Name is the system name.
    /// </summary>
    Outbound,

    /// <summary>One physical attempt to an external system, counted INSIDE its retry. More
    /// attempts than calls means the partner is being retried.</summary>
    OutboundAttempt,
```

Both fit the column's 16-character limit (`OutboundAttempt` is 15), and `Kind` is stored as text: no migration.

- [ ] **Step 5: Write the handler**

`src/Infrastructure/ExternalSystems/Http/OutboundTrafficHandler.cs`:

```csharp
using System.Net;
using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.ExternalSystems.Http;

/// <summary>
/// Counts calls to one external system. Attached twice per client: as Outbound outside the
/// resilience handler (one per call) and as OutboundAttempt inside it (one per attempt) — the
/// shape egdw_eghealth's OutboundTrafficHandler proved. Records in a finally, so a call that
/// throws is counted; a call the CALLER cancelled is not, because nothing about the partner was
/// learned from it. Never alters the request or response, never swallows an exception.
/// </summary>
internal sealed class OutboundTrafficHandler(
    ITrafficRecorder recorder, TimeProvider time, string systemName, TrafficKind kind) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var outcome = TrafficOutcome.Faulted;
        var record = true;
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            outcome = Classify(response.StatusCode);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            record = false;
            throw;
        }
        finally
        {
            if (record)
            {
                recorder.Record(kind, systemName, outcome, (long)time.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    private static TrafficOutcome Classify(HttpStatusCode status) => (int)status switch
    {
        >= 500 => TrafficOutcome.Faulted,
        >= 400 => TrafficOutcome.Failed,
        _ => TrafficOutcome.Succeeded,
    };
}
```

- [ ] **Step 6: Filter the reader to inbound kinds**

In `src/Infrastructure/Monitoring/TrafficReader.cs`, add to the class:

```csharp
    /// <summary>
    /// The traffic page's numbers are the application's OWN work. Outbound calls are a different
    /// question — how a partner is doing — and summing them in would double-count every request
    /// that makes one (and triple-count it with attempts). ADR 0031.
    /// </summary>
    private static readonly TrafficKind[] InboundKinds = [TrafficKind.Http, TrafficKind.Command, TrafficKind.Query];
```

and change the `Where` in **both** `SummarizeAsync` and `SeriesAsync` to:

```csharp
            .Where(bucket => bucket.BucketStart >= since && InboundKinds.Contains(bucket.Kind))
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~OutboundTrafficHandlerTests|FullyQualifiedName~TrafficReaderTests|FullyQualifiedName~Traffic"`
Expected: all PASS, including the existing traffic tests.

- [ ] **Step 8: Regenerate the API contract**

`TrafficKind` is on the wire (`TrafficRowDto.Kind`), so its two new members change `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts`. Run, from the repo root (bash):

```bash
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false Admin__ReconcileOnStart=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
git diff --stat openapi frontend/src/api
```

Expected: both files change, and the diff is only `"Outbound"` and `"OutboundAttempt"` added to `TrafficKind`. Then `npm run build --prefix frontend` must still pass (nothing switches exhaustively on `kind`; `frontend/src/test/handlers.ts` only uses `'Http'`).

- [ ] **Step 9: Commit**

```bash
git add src/Application/Abstractions/Traffic.cs src/Infrastructure tests/Infrastructure.Tests openapi frontend/src/api
git commit -m "feat(integrations): count outbound calls and attempts, kept out of the traffic page's totals"
```

---

### Task 7: OAuth 2.0 — token clients, private_key_jwt, the token check

This task contains §5's probes 1 and 3. If any expectation below about Duende.AccessTokenManagement 4.2.0's API is wrong, adjust the code to the package's real API, keep the tests' **behaviour** unchanged, and record what differed — it goes into ADR 0031 in Task 10.

**Files:**
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj`
- Create: `src/Infrastructure/ExternalSystems/Auth/PrivateKeyJwtAssertionService.cs`
- Create: `src/Infrastructure/ExternalSystems/Health/TokenHealthCheck.cs`
- Modify: `src/Infrastructure/ExternalSystems/ExternalSystemsRegistration.cs`
- Modify: `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj` (Testcontainers.Keycloak)
- Create: `tests/Infrastructure.Tests/ExternalSystems/KeycloakFixture.cs`
- Test: `tests/Infrastructure.Tests/ExternalSystems/PrivateKeyJwtAssertionServiceTests.cs`
- Test: `tests/Infrastructure.Tests/ExternalSystems/TokenAcquisitionTests.cs`

**Interfaces:**
- Consumes: `ICertificateProvider`, `ExternalSystemHandlerFactory.CreateTokenEndpointHandler`, `ExternalSystemNames.TokenBackchannel/TokenCheck`, `TestPki`.
- Produces: one Duende client-credentials client per system with `Auth.Kind != None`, registered under `ClientCredentialsClientName.Parse(systemName)`; `TokenHealthCheck`; `KeycloakFixture { Pki; TokenEndpoint; Issuer; const SecretClientId = "secret-client"; const SecretClientSecret; const JwtClientId = "jwt-client"; string WriteSecretFile(string directory); string WriteJwtClientPfx(string directory) }` and `[Collection(nameof(KeycloakCollection))]`.

- [ ] **Step 1: Add the packages**

`src/Infrastructure/AiFramework.Infrastructure.csproj`:

```xml
    <!--
      Client-credentials token acquisition, caching and refresh for external systems (ADR 0031).
      The client-side library only — Apache 2.0 — not Duende IdentityServer. Keycloak issues the
      tokens; this fetches them.
    -->
    <PackageReference Include="Duende.AccessTokenManagement" Version="4.2.0" />
    <!-- Signs private_key_jwt client assertions (ExternalSystems/Auth). -->
    <PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" Version="<resolved version>" />
```

For `<resolved version>`: first add only Duende, run `dotnet restore src/Infrastructure` then `dotnet list src/Infrastructure package --include-transitive | grep -i IdentityModel.JsonWebTokens`. If it is already transitive, pin exactly that version (explicit and public, for the NU1605/MSB3277 reason the csproj's `Relational` comment gives). If it is absent, use the latest stable 8.x and re-run `dotnet build src/Api src/Worker` to confirm no conflict.

`tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj`:

```xml
    <PackageReference Include="Testcontainers.Keycloak" Version="4.15.0" />
```

Run `dotnet build AiFramework.slnx`. Expected: builds.

- [ ] **Step 2: Write the Keycloak fixture**

`tests/Infrastructure.Tests/ExternalSystems/KeycloakFixture.cs`:

```csharp
using System.Text;
using AiFramework.PartnerSimulator;
using Testcontainers.Keycloak;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

/// <summary>
/// One Keycloak per test run (tests/CLAUDE.md: one container per collection). The realm is
/// generated at start-up because the private_key_jwt client must carry a certificate that only
/// exists once this run's TestPki does — nothing here is ever committed.
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "aiframework";
    public const string SecretClientId = "secret-client";
    public const string SecretClientSecret = "test-only-secret";
    public const string JwtClientId = "jwt-client";

    private readonly KeycloakContainer _container;
    private readonly byte[] _jwtClientPfx;

    public KeycloakFixture()
    {
        Pki = TestPki.Create("AiFramework Keycloak Test");
        var jwtClient = Pki.IssueClient(JwtClientId);
        _jwtClientPfx = Pki.ExportPfx(jwtClient, password: null);

        _container = new KeycloakBuilder()
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(RealmJson(Convert.ToBase64String(jwtClient.RawData))),
                "/opt/keycloak/data/import/aiframework-realm.json")
            .WithCommand("--import-realm")
            .Build();
    }

    public TestPki Pki { get; }

    public string Issuer => $"{_container.GetBaseAddress().TrimEnd('/')}/realms/{Realm}";

    public string TokenEndpoint => $"{Issuer}/protocol/openid-connect/token";

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        Pki.Dispose();
    }

    public string WriteSecretFile(string directory)
    {
        var path = Path.Combine(directory, "client-secret");
        File.WriteAllText(path, SecretClientSecret + "\n");
        return path;
    }

    public string WriteJwtClientPfx(string directory)
    {
        var path = Path.Combine(directory, "jwt-client.pfx");
        File.WriteAllBytes(path, _jwtClientPfx);
        return path;
    }

    private static string RealmJson(string certificateBase64) => $$"""
        {
          "realm": "{{Realm}}",
          "enabled": true,
          "clients": [
            {
              "clientId": "{{SecretClientId}}",
              "enabled": true,
              "publicClient": false,
              "serviceAccountsEnabled": true,
              "standardFlowEnabled": false,
              "clientAuthenticatorType": "client-secret",
              "secret": "{{SecretClientSecret}}"
            },
            {
              "clientId": "{{JwtClientId}}",
              "enabled": true,
              "publicClient": false,
              "serviceAccountsEnabled": true,
              "standardFlowEnabled": false,
              "clientAuthenticatorType": "client-jwt",
              "attributes": {
                "use.jwks.url": "false",
                "jwt.credential.certificate": "{{certificateBase64}}",
                "token.endpoint.auth.signing.alg": "RS256"
              }
            }
          ]
        }
        """;
}

// CA1711: the name ends in "Collection" without implementing ICollection<T>. This is the xUnit
// collection-definition naming convention (PostgresCollection does the same), not a collection
// type, so the rule's intent does not apply here.
#pragma warning disable CA1711
[CollectionDefinition(nameof(KeycloakCollection))]
public sealed class KeycloakCollection : ICollectionFixture<KeycloakFixture>;
#pragma warning restore CA1711
```

- [ ] **Step 3: Write the failing assertion tests**

`tests/Infrastructure.Tests/ExternalSystems/PrivateKeyJwtAssertionServiceTests.cs`:

```csharp
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
```

- [ ] **Step 4: Write the failing Keycloak tests (probes 1 and 3 start here)**

`tests/Infrastructure.Tests/ExternalSystems/TokenAcquisitionTests.cs`:

```csharp
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

    private async Task<HealthReportEntry> TokenCheckAsync(IDictionary<string, string?> auth)
    {
        var config = new Dictionary<string, string?> { ["Systems:Sim:BaseAddress"] = "https://partner.example/" };
        foreach (var (key, value) in auth)
        {
            config[$"Systems:Sim:{key}"] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(config));
        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Name == "Sim:token");
        return report.Entries["Sim:token"];
    }

    [Fact]
    public async Task CheckHealth_WithTheRightClientSecret_IsHealthy()
    {
        var entry = await TokenCheckAsync(new Dictionary<string, string?>
        {
            ["Auth:Kind"] = "ClientSecret",
            ["Auth:TokenEndpoint"] = keycloak.TokenEndpoint,
            ["Auth:ClientId"] = KeycloakFixture.SecretClientId,
            ["Auth:ClientSecretFile"] = keycloak.WriteSecretFile(_directory),
        });

        entry.Status.Should().Be(HealthStatus.Healthy, entry.Description);
    }

    [Fact]
    public async Task CheckHealth_WithAWrongClientSecret_IsUnhealthyWithoutTheSecret()
    {
        var secret = Path.Combine(_directory, "wrong");
        await File.WriteAllTextAsync(secret, "not-the-secret");

        var entry = await TokenCheckAsync(new Dictionary<string, string?>
        {
            ["Auth:Kind"] = "ClientSecret",
            ["Auth:TokenEndpoint"] = keycloak.TokenEndpoint,
            ["Auth:ClientId"] = KeycloakFixture.SecretClientId,
            ["Auth:ClientSecretFile"] = secret,
        });

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().NotContain("not-the-secret");
    }

    // §5 probe 3: Keycloak's "Signed JWT" authenticator accepts our assertion with the
    // certificate registered on the client (not a JWKS URL), audience = the realm issuer.
    [Fact]
    public async Task CheckHealth_WithPrivateKeyJwt_IsHealthy()
    {
        var entry = await TokenCheckAsync(new Dictionary<string, string?>
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

        var entry = await TokenCheckAsync(new Dictionary<string, string?>
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
```

- [ ] **Step 5: Run to verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~PrivateKeyJwtAssertionServiceTests|FullyQualifiedName~TokenAcquisitionTests"`
Expected: build FAILS — `PrivateKeyJwtAssertionService` does not exist. (Needs Docker running.)

- [ ] **Step 6: Write the assertion service**

`src/Infrastructure/ExternalSystems/Auth/PrivateKeyJwtAssertionService.cs`:

```csharp
using System.Security.Cryptography;
using AiFramework.Infrastructure.ExternalSystems.Certificates;
using Duende.AccessTokenManagement;
using Duende.IdentityModel;
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
            // Null makes the token request fail at Keycloak (401), which the token check reports;
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
            Claims = new Dictionary<string, object>
            {
                [JwtClaimTypes.Subject] = clientId,
                [JwtClaimTypes.JwtId] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new X509SigningCredentials(certificate, AlgorithmFor(certificate)),
        };

        return Task.FromResult<ClientAssertion?>(new ClientAssertion
        {
            Type = OidcConstants.ClientAssertionTypes.JwtBearer,
            Value = new JsonWebTokenHandler().CreateToken(descriptor),
        });
    }

    private static string AlgorithmFor(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) =>
        certificate.GetECDsaPublicKey() is not null ? SecurityAlgorithms.EcdsaSha256 : SecurityAlgorithms.RsaSha256;

    [LoggerMessage(Level = LogLevel.Warning, Message = "No client assertion for {System}: {Problem}")]
    private partial void LogNoCertificate(string system, string problem);
}
```

`GetECDsaPublicKey()` returns an `ECDsa` that should be disposed; if the analyzer flags it (CA2000), use `using var ec = certificate.GetECDsaPublicKey(); return ec is not null ? … : …;`.

- [ ] **Step 7: Write the token health check**

`src/Infrastructure/ExternalSystems/Health/TokenHealthCheck.cs`:

```csharp
using Duende.AccessTokenManagement;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.ExternalSystems.Health;

/// <summary>
/// Can this system's token be obtained. Reads through Duende's cache, so a live token costs the
/// IdP nothing; an expired one is fetched exactly as a real call would. The failure description
/// is Duende's error code only — never a secret, an assertion or a token.
/// </summary>
internal sealed class TokenHealthCheck(IClientCredentialsTokenManager tokens, string systemName) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = await tokens
            .GetAccessTokenAsync(ClientCredentialsClientName.Parse(systemName), ct: cancellationToken)
            .ConfigureAwait(false);

        return result.WasSuccessful(out _, out var failure)
            ? HealthCheckResult.Healthy($"{systemName} token available")
            : HealthCheckResult.Unhealthy($"{systemName} token unavailable: {failure.Error}");
    }
}
```

API names to verify against 4.2.0: the token manager service (`IClientCredentialsTokenManager` in v4; `IClientCredentialsTokenManagementService` in v3), its `GetAccessTokenAsync` parameters, and how its result exposes success/failure (`WasSuccessful(out token, out failure)` / `Succeeded`). Use what the package actually exposes; the check's behaviour stays: success → Healthy, failure → Unhealthy with the error code only.

- [ ] **Step 8: Register token management**

In `AddExternalSystems`, inside the `foreach` (after the certificate check), add:

```csharp
            if (system.Auth.Kind != ExternalSystemAuthKind.None)
            {
                AddTokenClient(services, name, system);
                health.Add(new HealthCheckRegistration(
                    ExternalSystemNames.TokenCheck(name),
                    sp => ActivatorUtilities.CreateInstance<TokenHealthCheck>(sp, name),
                    failureStatus: null,
                    tags));
            }
```

and before the `foreach`:

```csharp
        services.AddSingleton<IClientAssertionService, PrivateKeyJwtAssertionService>();
```

Add the method to `ExternalSystemsRegistration`:

```csharp
    private static void AddTokenClient(IServiceCollection services, string name, ExternalSystemOptions system)
    {
        // The token endpoint presents the same client certificate as the system's own calls, for
        // Keycloak's X.509 authenticator and RFC 8705 certificate-bound tokens.
        services.AddHttpClient(ExternalSystemNames.TokenBackchannel(name))
            .ConfigurePrimaryHttpMessageHandler(sp =>
                sp.GetRequiredService<ExternalSystemHandlerFactory>().CreateTokenEndpointHandler(name));

        services.AddClientCredentialsTokenManagement()
            .AddClient(ClientCredentialsClientName.Parse(name), client =>
            {
                // Runs when the options are first resolved, not now: the secret file is read
                // lazily, so a missing file is that system's token failure, not a startup crash.
                // A ROTATED secret needs a restart — VSO's rolloutRestartTargets provides it.
                client.TokenEndpoint = new Uri(system.Auth.TokenEndpoint!); // validated absolute for Kind != None.
                client.ClientId = ClientId.Parse(system.Auth.ClientId!); // validated non-empty for Kind != None.
                client.HttpClientName = ExternalSystemNames.TokenBackchannel(name);
                client.ClientCredentialStyle = system.Auth.CredentialStyle == ExternalSystemCredentialStyle.PostBody
                    ? ClientCredentialStyle.PostBody
                    : ClientCredentialStyle.AuthorizationHeader;
                if (!string.IsNullOrWhiteSpace(system.Auth.Scope))
                {
                    client.Scope = Scope.Parse(system.Auth.Scope);
                }

                if (system.Auth.Kind == ExternalSystemAuthKind.ClientSecret
                    && TryReadSecret(system.Auth.ClientSecretFile!) is { } secret) // validated for ClientSecret.
                {
                    client.ClientSecret = ClientSecret.Parse(secret);
                }
            });
    }

    // A missing secret leaves ClientSecret unset: Keycloak answers 401 and the token check
    // reports it. Throwing from inside an options callback would fail the first CALL instead.
    private static string? TryReadSecret(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
```

Duende 4.x caches tokens in `HybridCache`. The hosts already register one (`AddCaching`, from `AddInfrastructure`); a bare `ServiceCollection` in a test may not. If resolving the token manager fails for a missing `HybridCache`, add `services.AddHybridCache();` to the test's arrange (`TokenAcquisitionTests`, `ExternalSystemClientTests`, `HandlerChainTests`) — never inside `AddExternalSystems`, which would register a second one in the hosts.

Usings: `Duende.AccessTokenManagement`, `Duende.IdentityModel.Client` (for `ClientCredentialStyle`), `AiFramework.Infrastructure.ExternalSystems.Auth`. If `AddClientCredentialsTokenManagement()` must only be called once, hoist it above the `foreach` into a variable and call `.AddClient(...)` on it per system — check the package; calling it once is the safe form either way.

- [ ] **Step 9: Run the tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~PrivateKeyJwtAssertionServiceTests|FullyQualifiedName~TokenAcquisitionTests"`
Expected: all PASS. **If `CheckHealth_WithPrivateKeyJwt_IsHealthy` fails** (probe 3), read the token response's `error_description` (temporarily log it, never commit that log): Keycloak may want the `kid` header to match, the audience to be the token endpoint, or `jwt.credential.kid`. Fix in this order — never by weakening the audience to the token endpoint without recording why in ADR 0031: (a) set `descriptor.AdditionalHeaderClaims` `kid` to what Keycloak computes; (b) only if Keycloak's version rejects the issuer as audience, configure the realm attribute that allows it. Record the outcome either way.

- [ ] **Step 10: Commit**

```bash
git add src/Infrastructure tests/Infrastructure.Tests
git commit -m "feat(integrations): fetch client-credentials tokens from Keycloak with a secret or private_key_jwt"
```

---

### Task 8: Typed clients — the full handler chain

This task contains §5's probe 1 (one refresh on 401).

**Files:**
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj` (Refit)
- Create: `src/Infrastructure/ExternalSystems/ExternalSystemClientSettings.cs`
- Create: `src/Infrastructure/ExternalSystems/ExternalSystemClientBuilder.cs`
- Modify: `src/Infrastructure/ExternalSystems/ExternalSystemsBuilder.cs` (`AddClient<TApi>`)
- Test: `tests/Infrastructure.Tests/ExternalSystems/ExternalSystemClientTests.cs`
- Test: `tests/Infrastructure.Tests/ExternalSystems/HandlerChainTests.cs`

**Interfaces:**
- Consumes: everything above; `ResilienceOptions.Enabled` (global test switch).
- Produces: `ExternalSystemsBuilder.AddClient<TApi>(string name) : ExternalSystemClientBuilder<TApi>`; `ExternalSystemClientBuilder<TApi>.WithoutRetry(string reason)`, `.WithAdapter<TPort, TAdapter>()`; `ExternalSystemClientSettings { string? RetryDisabledReason }` as named options keyed by system name.

- [ ] **Step 1: Add Refit**

`src/Infrastructure/AiFramework.Infrastructure.csproj`:

```xml
    <!-- Typed clients for external systems (ADR 0031). Source-generated at build time; methods
         return IApiResponse<T> and never throw ApiException, so adapters map the final outcome. -->
    <PackageReference Include="Refit.HttpClientFactory" Version="16.3.0" />
```

`tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj` gets the same reference (the test's Refit interface is source-generated in the test assembly).

- [ ] **Step 2: Write the failing end-to-end client tests**

`tests/Infrastructure.Tests/ExternalSystems/ExternalSystemClientTests.cs`:

```csharp
using System.Net;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.Resilience;
using AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Refit;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public interface ISimulatorApi
{
    [Get("/echo")]
    public Task<IApiResponse<EchoResponse>> EchoAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The whole chain, for real: Refit → traffic → resilience → traffic → Duende → mTLS, against the
/// simulator (trusting this run's Keycloak PKI) and a real Keycloak. Retry delays are real but
/// tiny (BaseDelay 50 ms) rather than faked, because Duende's cache also reads the clock and the
/// two must agree.
/// </summary>
[Collection(nameof(KeycloakCollection))]
public sealed class ExternalSystemClientTests(KeycloakFixture keycloak) : IAsyncLifetime, IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-client-").FullName;
    private readonly ITrafficRecorder _traffic = Substitute.For<ITrafficRecorder>();
    private PartnerSimulatorApp _simulator = null!; // set in InitializeAsync.

    public async Task InitializeAsync() =>
        _simulator = await PartnerSimulatorTests.StartAsync(keycloak.Pki, jwtAuthority: keycloak.Issuer);

    public async Task DisposeAsync() => await _simulator.DisposeAsync();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ServiceProvider Build(string authKind = "ClientSecret", Action<ExternalSystemClientBuilder<ISimulatorApi>>? client = null,
        bool resilienceEnabled = true)
    {
        File.WriteAllText(Path.Combine(_directory, "ca.pem"), keycloak.Pki.RootPem);
        var config = new Dictionary<string, string?>
        {
            ["Systems:Sim:BaseAddress"] = _simulator.BaseAddress.ToString(),
            ["Systems:Sim:Resilience:BaseDelay"] = "00:00:00.050",
            ["Systems:Sim:Resilience:MaxRetryAttempts"] = "2",
            ["Systems:Sim:ServerTrust:CaBundlePath"] = Path.Combine(_directory, "ca.pem"),
            ["Systems:Sim:ServerTrust:CheckRevocation"] = "false",
            ["Systems:Sim:ClientCertificate:Path"] = keycloak.WriteJwtClientPfx(_directory),
            ["Systems:Sim:Auth:Kind"] = authKind,
            ["Systems:Sim:Auth:TokenEndpoint"] = keycloak.TokenEndpoint,
            ["Systems:Sim:Auth:Issuer"] = keycloak.Issuer,
        };
        if (authKind == "ClientSecret")
        {
            config["Systems:Sim:Auth:ClientId"] = KeycloakFixture.SecretClientId;
            config["Systems:Sim:Auth:ClientSecretFile"] = keycloak.WriteSecretFile(_directory);
        }
        else
        {
            config["Systems:Sim:Auth:ClientId"] = KeycloakFixture.JwtClientId;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_traffic);
        services.AddResilience();
        services.Configure<ResilienceOptions>(o => o.Enabled = resilienceEnabled);
        var builder = services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(config))
            .AddClient<ISimulatorApi>("Sim");
        client?.Invoke(builder);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("ClientSecret")]
    [InlineData("PrivateKeyJwt")]
    public async Task Send_ThroughTheFullChain_ReachesThePartnerWithACertificateAndAToken(string authKind)
    {
        await using var provider = Build(authKind);

        var response = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        response.IsSuccessStatusCode.Should().BeTrue(response.Error?.Content);
        response.Content!.ClientCertificateSubject.Should().Be($"CN={KeycloakFixture.JwtClientId}"); // success has content.
        _simulator.EchoRequests.Should().ContainSingle().Which.Authorization.Should().StartWith("Bearer ");
    }

    [Fact]
    public async Task Send_WhenThePartnerAnswers503Once_RetriesAndRecordsOneCallAndTwoAttempts()
    {
        await using var provider = Build();
        _simulator.EnqueueEchoStatus(HttpStatusCode.ServiceUnavailable);

        var response = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        response.IsSuccessStatusCode.Should().BeTrue();
        _traffic.Received(1).Record(TrafficKind.Outbound, "Sim", TrafficOutcome.Succeeded, Arg.Any<long>());
        _traffic.Received(1).Record(TrafficKind.OutboundAttempt, "Sim", TrafficOutcome.Faulted, Arg.Any<long>());
        _traffic.Received(1).Record(TrafficKind.OutboundAttempt, "Sim", TrafficOutcome.Succeeded, Arg.Any<long>());
    }

    // §5 probe 1.
    [Fact]
    public async Task Send_WhenThePartnerAnswers401Once_RefreshesTheTokenOnceAndSucceeds()
    {
        await using var provider = Build();
        var api = provider.GetRequiredService<ISimulatorApi>();
        await api.EchoAsync(CancellationToken.None); // warm the token cache with token A.
        _simulator.EnqueueEchoStatus(HttpStatusCode.Unauthorized);

        var response = await api.EchoAsync(CancellationToken.None);

        response.IsSuccessStatusCode.Should().BeTrue();
        var authorizations = _simulator.EchoRequests.Select(r => r.Authorization).ToList();
        authorizations.Should().HaveCount(3, "the warm-up, the 401, and exactly one resend");
        authorizations[2].Should().NotBe(authorizations[1], "the resend must carry a freshly fetched token");
    }

    [Fact]
    public async Task Send_WithoutRetry_MakesExactlyOneAttemptOnA503()
    {
        await using var provider = Build(client: c => c.WithoutRetry("CreateThing is not idempotent"));
        _simulator.EnqueueEchoStatus(HttpStatusCode.ServiceUnavailable);

        var response = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _simulator.EchoRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Send_WithResilienceDisabledGlobally_MakesExactlyOneAttemptOnA503()
    {
        await using var provider = Build(resilienceEnabled: false);
        _simulator.EnqueueEchoStatus(HttpStatusCode.ServiceUnavailable);

        var response = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _simulator.EchoRequests.Should().ContainSingle();
    }

    [Fact]
    public void WithoutRetry_WithoutAReason_Throws()
    {
        var act = () => Build(client: c => c.WithoutRetry(" "));

        act.Should().Throw<ArgumentException>();
    }
}
```

`tests/Infrastructure.Tests/ExternalSystems/HandlerChainTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.ExternalSystems.Http;
using AiFramework.Infrastructure.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using NSubstitute;
using Refit;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class HandlerChainTests
{
    [Fact]
    public void AddClient_BuildsTheChainInTheFixedOrder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ITrafficRecorder>());
        services.AddResilience();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>
        {
            ["Systems:Sim:BaseAddress"] = "https://partner.example/",
            ["Systems:Sim:Auth:Kind"] = "ClientSecret",
            ["Systems:Sim:Auth:TokenEndpoint"] = "https://idp.example/token",
            ["Systems:Sim:Auth:ClientId"] = "client",
            ["Systems:Sim:Auth:ClientSecretFile"] = "absent",
        })).AddClient<ISimulatorApi>("Sim");
        using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(UniqueName.ForType<ISimulatorApi>());

        Describe(handler).Should().ContainInOrder(
            "Traffic:Outbound", nameof(ResilienceHandler), "Traffic:OutboundAttempt", "Token", nameof(SocketsHttpHandler));
    }

    private static List<string> Describe(HttpMessageHandler handler)
    {
        var chain = new List<string>();
        for (HttpMessageHandler? current = handler; current is not null;
             current = (current as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(current switch
            {
                OutboundTrafficHandler traffic => $"Traffic:{traffic.Kind}",
                ResilienceHandler => nameof(ResilienceHandler),
                SocketsHttpHandler => nameof(SocketsHttpHandler),
                _ when current.GetType().Name.Contains("Token", StringComparison.Ordinal) => "Token",
                _ => current.GetType().Name,
            });
        }

        return chain;
    }
}
```

This needs `OutboundTrafficHandler` to expose `public TrafficKind Kind => kind;` — add it in Step 4. `UniqueName.ForType<T>()` is Refit's own client-name helper; if Refit 16 names it differently, use whatever `AddRefitClient<T>` registers (its XML docs say). `ResilienceHandler` is `Microsoft.Extensions.Http.Resilience.ResilienceHandler`.

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ExternalSystemClientTests|FullyQualifiedName~HandlerChainTests"`
Expected: build FAILS — `AddClient<TApi>` does not exist.

- [ ] **Step 4: Write the client settings and builder**

Add to `OutboundTrafficHandler`: `public TrafficKind Kind => kind;` (read by `HandlerChainTests`).

`src/Infrastructure/ExternalSystems/ExternalSystemClientSettings.cs`:

```csharp
namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>Code-level settings for one system's typed client, as named options keyed by system name.</summary>
internal sealed class ExternalSystemClientSettings
{
    /// <summary>Non-null: this client never retries, for this reason (ADR 0014's non-idempotent rule).</summary>
    public string? RetryDisabledReason { get; set; }
}
```

`src/Infrastructure/ExternalSystems/ExternalSystemClientBuilder.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>Per-client options, applied through named options so no terminal "Build()" can be forgotten.</summary>
public sealed class ExternalSystemClientBuilder<TApi>
    where TApi : class
{
    internal ExternalSystemClientBuilder(IServiceCollection services, string systemName)
    {
        Services = services;
        SystemName = systemName;
    }

    public IServiceCollection Services { get; }

    public string SystemName { get; }

    /// <summary>
    /// This client's calls are not idempotent and the partner takes no idempotency key: never
    /// retry. The reason is mandatory and is logged at startup. ADR 0014.
    /// </summary>
    public ExternalSystemClientBuilder<TApi> WithoutRetry(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Services.Configure<ExternalSystemClientSettings>(SystemName, s => s.RetryDisabledReason = reason);
        return this;
    }

    /// <summary>The Application port this partner implements, and its adapter.</summary>
    public ExternalSystemClientBuilder<TApi> WithAdapter<TPort, TAdapter>()
        where TPort : class
        where TAdapter : class, TPort
    {
        Services.AddScoped<TPort, TAdapter>();
        return this;
    }
}
```

- [ ] **Step 5: Write `AddClient<TApi>`**

Add to `ExternalSystemsBuilder`:

```csharp
    /// <summary>
    /// A Refit client for one system, with the fixed chain, outermost first:
    /// Outbound traffic → standard resilience (this system's options and breaker) →
    /// OutboundAttempt traffic → Duende's 401-resend and token handler (when Auth.Kind != None) →
    /// the primary handler presenting the certificate. ADR 0031; order asserted by HandlerChainTests.
    /// </summary>
    /// <remarks>
    /// An unconfigured name still registers: its primary handler fails fast as "not configured",
    /// which the adapter maps to Unavailable — so an environment without a partner degrades one
    /// feature instead of refusing to start.
    /// </remarks>
    public ExternalSystemClientBuilder<TApi> AddClient<TApi>(string name)
        where TApi : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var system = Snapshot.Find(name);

        var client = Services.AddRefitClient<TApi>()
            .ConfigureHttpClient((sp, http) =>
            {
                var current = sp.GetRequiredService<IOptionsMonitor<ExternalSystemsOptions>>().CurrentValue.Find(name);
                http.BaseAddress = new Uri($"{(current?.BaseAddress ?? "https://not-configured.invalid").TrimEnd('/')}/");
                http.Timeout = Timeout.InfiniteTimeSpan; // the standard handler owns both timeouts.
            })
            .AddHttpMessageHandler(sp => new OutboundTrafficHandler(
                sp.GetRequiredService<ITrafficRecorder>(), sp.GetRequiredService<TimeProvider>(), name, TrafficKind.Outbound));

        client.AddStandardResilienceHandler().Configure((resilience, sp) =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<ExternalSystemsOptions>>().CurrentValue.Find(name)
                ?? new ExternalSystemOptions();
            resilience.TotalRequestTimeout.Timeout = options.Resilience.TotalRequestTimeout;
            resilience.AttemptTimeout.Timeout = options.Resilience.AttemptTimeout;
            resilience.Retry.MaxRetryAttempts = options.Resilience.MaxRetryAttempts;
            resilience.Retry.Delay = options.Resilience.BaseDelay;

            var globallyEnabled = sp.GetRequiredService<IOptions<ResilienceOptions>>().Value.Enabled;
            var disabledReason = sp.GetRequiredService<IOptionsMonitor<ExternalSystemClientSettings>>().Get(name).RetryDisabledReason;
            if (!globallyEnabled || disabledReason is not null)
            {
                // Never MaxRetryAttempts = 0: Polly's own validation forbids it. ADR 0014.
                resilience.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
            }
        });

        client.AddHttpMessageHandler(sp => new OutboundTrafficHandler(
            sp.GetRequiredService<ITrafficRecorder>(), sp.GetRequiredService<TimeProvider>(), name, TrafficKind.OutboundAttempt));

        if (system is { Auth.Kind: not ExternalSystemAuthKind.None })
        {
            client.AddDefaultAccessTokenResiliency()
                .AddClientCredentialsTokenHandler(ClientCredentialsClientName.Parse(name));
        }

        client.ConfigurePrimaryHttpMessageHandler(sp =>
            sp.GetRequiredService<ExternalSystemHandlerFactory>().CreatePrimaryHandler(name));

        return new ExternalSystemClientBuilder<TApi>(Services, name);
    }
```

Usings: `AiFramework.Application.Abstractions`, `AiFramework.Infrastructure.ExternalSystems.Http`, `AiFramework.Infrastructure.Resilience`, `Duende.AccessTokenManagement`, `Microsoft.Extensions.Options`, `Refit`. `AddResilience()` must have been called (it is, in `AddInfrastructure`); the tests call it explicitly.

`AddDefaultAccessTokenResiliency()` is Duende's 401 resend for clients wired by hand (its docs: "When you use AddClientCredentialsHttpClient … retries … 401; for manually configured clients …AddDefaultAccessTokenResiliency()"). If 4.2.0 names it differently, use the equivalent — `Send_WhenThePartnerAnswers401Once_…` is the arbiter.

Log the retry-disabled reason once at startup: in `AddClient`, after `WithoutRetry` is applied there is no logger yet, so add a tiny `IHostedService`-free approach instead — log it inside the `Configure` callback above the first time it runs for this client (the callback runs when the pipeline is first built), via `sp.GetRequiredService<ILoggerFactory>().CreateLogger("AiFramework.ExternalSystems").LogInformation(...)` wrapped in a `[LoggerMessage]` static partial method on `ExternalSystemsBuilder` (make the class `partial`). Message: `"Retry is disabled for {System}: {Reason}"`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ExternalSystemClientTests|FullyQualifiedName~HandlerChainTests"`
Expected: all PASS. **If the 401 test fails** (probe 1): if the request count is 2, the resend is not wired — check `AddDefaultAccessTokenResiliency`'s position (it must be outside the token handler); if the resend carries the same token, the forced refresh is not happening — check Duende's docs for the 4.2.0 mechanism. Record the finding for ADR 0031.

- [ ] **Step 7: Run every Infrastructure test**

Run: `dotnet test tests/Infrastructure.Tests`
Expected: all PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Infrastructure tests/Infrastructure.Tests
git commit -m "feat(integrations): build typed external-system clients with traffic, retry, tokens and mTLS"
```

---

### Task 9: Wire both hosts, and keep partners out of readiness

**Files:**
- Modify: `src/Api/Program.cs` (around lines 314 and 444)
- Modify: `src/Worker/Program.cs` (around lines 65 and 118)
- Modify: `tests/Api.IntegrationTests/ApiFactory.cs`, `tests/Api.IntegrationTests/ReadinessTests.cs`
- Modify: `tests/Worker.IntegrationTests/WorkerFactory.cs`
- Create: `tests/Worker.IntegrationTests/ReadinessTests.cs`

**Interfaces:**
- Consumes: `AddExternalSystems`, `ExternalSystemsOptions.SectionName`, `ExternalSystemHealth.IsNotExternal`.

- [ ] **Step 1: Configure an unreachable system in both test factories**

In `ApiFactory.ConfigureWebHost`, beside the other `UseSetting` calls:

```csharp
        // A permanently unreachable external system (port 1 on loopback refuses at once). Its
        // probe is Unhealthy by construction, which is what makes ReadinessTests' 200 meaningful:
        // /health/ready must ignore it. ADR 0031.
        builder.UseSetting("ExternalSystems:Systems:Unreachable:BaseAddress", "https://127.0.0.1:1/");
```

Add the same line to `WorkerFactory.ConfigureWebHost`.

- [ ] **Step 2: Write the failing readiness tests**

Append to `tests/Api.IntegrationTests/ReadinessTests.cs`:

```csharp
    [Fact]
    public async Task GetReady_WithAnUnreachableExternalSystem_StillReturns200()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExternalSystemCheck_ForTheUnreachableSystem_IsRegisteredAndUnhealthy()
    {
        var health = factory.Services.GetRequiredService<HealthCheckService>();

        var report = await health.CheckHealthAsync(c => c.Name == "Unreachable");

        report.Entries["Unreachable"].Status.Should().Be(HealthStatus.Unhealthy,
            "otherwise the 200 above would prove nothing");
    }
```

with `using Microsoft.Extensions.DependencyInjection;` and `using Microsoft.Extensions.Diagnostics.HealthChecks;`.

`tests/Worker.IntegrationTests/ReadinessTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Worker.IntegrationTests;

[Collection(nameof(WorkerFactoryCollection))]
public sealed class ReadinessTests(WorkerFactory factory)
{
    [Fact]
    public async Task GetReady_WithAnUnreachableExternalSystem_StillReturns200()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExternalSystemCheck_ForTheUnreachableSystem_IsRegisteredAndUnhealthy()
    {
        var health = factory.Services.GetRequiredService<HealthCheckService>();

        var report = await health.CheckHealthAsync(c => c.Name == "Unreachable");

        report.Entries["Unreachable"].Status.Should().Be(HealthStatus.Unhealthy);
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~ReadinessTests"` and the same for `tests/Worker.IntegrationTests`.
Expected: `ExternalSystemCheck_…` FAILS with `KeyNotFoundException` (nothing registers the system yet).

- [ ] **Step 4: Wire the Api**

In `src/Api/Program.cs`, directly after the `builder.Services.Configure<ResilienceOptions>(...)` line:

```csharp
// External systems (ADR 0031): every configured system's probe client, health checks and token
// client. Here rather than in AddInfrastructure because the set of named clients must be known at
// registration time — the same reason the worker reads Jobs straight off configuration.
builder.Services.AddExternalSystems(builder.Configuration.GetSection(ExternalSystemsOptions.SectionName));
```

Replace `app.MapHealthChecks("/health/ready").DisableHttpMetrics();` with:

```csharp
// Partners are excluded: an external system's outage must never take this pod out of rotation.
// Without the predicate MapHealthChecks runs EVERY registered check. ADR 0031.
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = ExternalSystemHealth.IsNotExternal })
    .DisableHttpMetrics();
```

Usings: `AiFramework.Infrastructure.ExternalSystems`, `AiFramework.Infrastructure.ExternalSystems.Health`, `Microsoft.AspNetCore.Diagnostics.HealthChecks`.

- [ ] **Step 5: Wire the worker**

In `src/Worker/Program.cs`, after `builder.Services.Configure<ResilienceOptions>(...)`:

```csharp
// External systems, as in src/Api/Program.cs: both hosts get every client, because writes to a
// partner run here as jobs. ADR 0031.
builder.Services.AddExternalSystems(builder.Configuration.GetSection(ExternalSystemsOptions.SectionName));
```

Replace `app.MapHealthChecks("/health/ready");` with:

```csharp
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = ExternalSystemHealth.IsNotExternal });
```

Same usings.

- [ ] **Step 6: Run the readiness tests and both architecture suites**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~ReadinessTests|FullyQualifiedName~ArchitectureTests"` and the same for `tests/Worker.IntegrationTests`.
Expected: all PASS. `Program` is already in each host's composition allowlist; if an architecture test names a new type as depending on Infrastructure, it is in the wrong place — move the dependency into `Program.cs`, do not widen the allowlist.

- [ ] **Step 7: Confirm the contract and codegen are unaffected by this task**

Run the contract generation from Task 6 Step 8 again. Expected: no further diff (this task adds no endpoint). No Wolverine handler changed in this PR, so `codegen write` is not needed — `/verify` in Task 10 confirms.

- [ ] **Step 8: Commit**

```bash
git add src/Api/Program.cs src/Worker/Program.cs tests/Api.IntegrationTests tests/Worker.IntegrationTests
git commit -m "feat(integrations): register external systems in both hosts and keep them out of readiness"
```

---

### Task 10: Dev certificates, ADR 0031, the skill, and the docs

**Files:**
- Create: `scripts/new-dev-certs.ps1`
- Modify: `.gitignore`
- Create: `docs/adr/0031-outbound-integrations-with-external-systems.md`
- Create: `.claude/skills/external-systems/SKILL.md`
- Modify: `CLAUDE.md` (skills table), `src/Infrastructure/CLAUDE.md` (new section)
- Modify: `docs/superpowers/specs/2026-10-06-external-systems-design.md` (status line, probe outcomes)

- [ ] **Step 1: The dev-certificate script**

`scripts/new-dev-certs.ps1`:

```powershell
<#
.SYNOPSIS
    Generates a throwaway PKI for external-system development into .certs/ (git-ignored).
.DESCRIPTION
    Writes ca.pem, client.pfx/.pass and server.pfx/.pass using tests/PartnerSimulator's TestPki,
    so dev, CI and tests share one generator. Never commit the output. ADR 0031.
.PARAMETER Force
    Overwrite an existing .certs/ directory.
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root '.certs'

if ((Test-Path $out) -and -not $Force) {
    Write-Host ".certs/ already exists. Re-run with -Force to replace it."
    exit 0
}

if (Test-Path $out) { Remove-Item -Recurse -Force $out }

dotnet run --project (Join-Path $root 'tests/PartnerSimulator') -- generate-certs $out
if ($LASTEXITCODE -ne 0) { throw "Certificate generation failed." }
```

Append to `.gitignore` under `# --- Secrets ---`:

```
# Throwaway dev PKI from scripts/new-dev-certs.ps1 (ca.pem and .pass files are not covered by *.pfx)
.certs/
```

Run: `./scripts/new-dev-certs.ps1` then `git status --short`.
Expected: five files in `.certs/`; `git status` shows none of them.

- [ ] **Step 2: ADR 0031**

Use the `/adr` skill's format (read `docs/adr/0030-…md` for the house style: Date, Status, Context, Decision, Consequences, Alternatives considered). `docs/adr/0031-outbound-integrations-with-external-systems.md` must record, each as its own paragraph:

- **Context:** Danish public-sector APIs (OCES3, OIO OAuth) and internal services behind Keycloak; one outbound client today; egdw_eghealth as prior art and what was taken and rejected from it (spec's "Decided in conversation" row).
- **Decision:** Refit 16 returning `IApiResponse<T>`; Duende.AccessTokenManagement 4.2.0 (client library, Apache 2.0) with `private_key_jwt` preferred, audience = issuer; the fixed chain order and why each position; per-system options with secrets as file paths; probe with certificate but no token; three checks tagged `external`, excluded from readiness; `Outbound`/`OutboundAttempt` traffic excluded from the traffic page's totals; Vault-unaware via VSO.
- **The VSO contract:** one Kubernetes Secret per system, mounted read-only at `/var/run/secrets/external-systems/<system>/`, keys `client.pfx`, `client.pass`, optionally `client-secret` and `ca.pem`, `rolloutRestartTargets` naming the api and worker Deployments; config points `ClientCertificate:Path` etc. at those files.
- **Probe outcomes (§5):** 1 — the 401 resend mechanism found; 2 — whether CI's Linux `backend` job passed `Send_WithALeafIssuedByTheIntermediate_Succeeds` (fill in after the PR's first CI run, before merge); 3 — what Keycloak required for `private_key_jwt`; 4 — the worker already records traffic (`AddMonitoring` runs in every host).
- **Consequences:** contract change for `TrafficKind`; client-secret rotation needs a restart (certificate rotation does not); a `FailFastHandler` failure is retried by the standard handler before surfacing (harmless within the total timeout, but it delays the 503); per-pod breakers; `Resilience:Enabled=false` reaches every system.
- **Alternatives considered:** Kiota/NSwag; a hand-written token handler (egdw); app reads Vault directly (VaultSharp — last release targets .NET 8); Vault Agent sidecar; a separate `src/Integrations` project — with the trigger to revisit: **four or more partners, or a partner package whose dependencies Infrastructure should not carry**.

- [ ] **Step 3: The skill**

`.claude/skills/external-systems/SKILL.md`:

````markdown
---
name: external-systems
description: Use when adding or changing an outbound integration with an external system - Refit clients, OCES3/mTLS client certificates, OAuth 2.0 client credentials against Keycloak (client secret or private_key_jwt), per-system retry, the probe/certificate/token health checks, outbound traffic, ExternalSystems__Systems__* configuration, and the throwaway dev PKI.
---

# External systems

`src/Infrastructure/ExternalSystems/` holds the plumbing; a partner gets its own folder beside it
only when it has a real call. ADR 0031 has the reasoning.

## Adding a partner

1. Configure it under `ExternalSystems:Systems:<Name>` (env: `ExternalSystems__Systems__<Name>__BaseAddress`).
   Secrets are FILE PATHS: `ClientCertificate:Path`, `ClientCertificate:PasswordFile`, `Auth:ClientSecretFile`,
   `ServerTrust:CaBundlePath`. There is no option that holds a secret's value — do not add one.
2. In `src/Infrastructure/ExternalSystems/<Name>/`: an `internal` Refit interface whose methods return
   `IApiResponse<T>` (never `Task<T>`, which throws `ApiException`), the partner's DTOs, and an adapter
   implementing the Application port. The adapter converts the FINAL outcome to `Result<T>` exactly as
   `ExchangeRateClient` does: `HttpRequestException`, `TimeoutRejectedException`, `BrokenCircuitException` →
   `ErrorKind.Unavailable`; a caller's cancellation propagates.
3. Register inside `AddExternalSystems`: `builder.AddClient<IPartnerApi>("<Name>").WithAdapter<IPort, PartnerAdapter>()`.
4. A non-GET call runs only in a worker job and carries an idempotency key from the job's message id. If the
   partner takes none: `.WithoutRetry("why")`.

## Things that will cost you an afternoon

- **`/health/ready` must keep its predicate** (`ExternalSystemHealth.IsNotExternal`) in BOTH hosts. Without it,
  one partner outage pulls every pod. `ReadinessTests` in both integration projects guard it.
- **Never a certificate validation callback.** Server trust is `ServerTrust:CaBundlePath` →
  `CertificateChainPolicy` with `CustomRootTrust`. `NoAcceptAnyCertificateTests` scans `src/` for callbacks.
- **The PFX must contain the intermediates.** Linux sends only what the PFX holds; Windows can fill gaps from
  its store, so a Windows green proves nothing about the chain. CI is the evidence.
- **private_key_jwt audience is the IdP's ISSUER**, not its token endpoint (`Auth:Issuer`).
- **A missing certificate does not stop the host.** That system fails fast (`FailFastHandler`) and reports
  Unhealthy; check `/api/monitoring/external-systems` (PR 3) or the `<Name>:certificate` health entry.
- **Client-secret rotation needs a restart** (Duende reads the secret once); VSO's `rolloutRestartTargets`
  provides it. Certificate rotation does not — the provider re-reads the file every two minutes.
- **Outbound traffic is not on the traffic page's totals.** `TrafficReader` counts inbound kinds only.

## Local development

`./scripts/new-dev-certs.ps1` writes a throwaway PKI to `.certs/` (git-ignored). Tests generate their own
in memory (`TestPki`) and run a real Keycloak via Testcontainers (`KeycloakFixture`), so `dotnet test` needs Docker.
````

- [ ] **Step 4: CLAUDE.md files**

Root `CLAUDE.md`, skills table, after the `resilience` row:

```markdown
| `external-systems` | Calling an external system: Refit clients, OCES3/mTLS, OAuth 2.0 client credentials (Keycloak), per-system retry and health checks, `ExternalSystems__*` config, the dev PKI |
```

`src/Infrastructure/CLAUDE.md`, a new section after `## Resilience`:

```markdown
## External systems

`ExternalSystems/` is the plumbing every outbound integration shares; the `external-systems` skill has the
how-to and ADR 0031 the why. Rules that are easy to break:

- **Every client goes through `ExternalSystemsBuilder.AddClient<TApi>`**, which fixes the chain: Outbound
  traffic → standard resilience → OutboundAttempt traffic → Duende token handler → the certificate-presenting
  primary handler. `HandlerChainTests` asserts the order. A hand-wired `AddHttpClient` for a partner skips
  traffic, per-system retry and the certificate, silently.
- **Names are composed only in `ExternalSystemNames`.** The system name is the config key, health-check name,
  traffic name and token client name; composing one elsewhere lets them drift with no error.
- **No certificate validation callback, anywhere.** Server trust is `CertificateChainPolicy`.
- **Secrets are file paths in options, never values.**
```

- [ ] **Step 5: Update the spec**

In `docs/superpowers/specs/2026-10-06-external-systems-design.md`: set **Status** to `Plumbing built (PR 2); monitoring (PR 3) pending.` and, under §5, add one line per probe with its outcome (same text as the ADR).

- [ ] **Step 6: Format and verify**

Run: `dotnet format whitespace AiFramework.slnx` then invoke the `/verify` skill.
Expected: both stacks build (Debug and Release), every test suite passes, lint clean, `codegen` diff empty, `contract` diff matches what Task 6 committed.

- [ ] **Step 7: Review**

Dispatch the `dotnet-reviewer` agent on the branch diff. Fix what it finds, re-run `/verify`.

- [ ] **Step 8: Commit and open the PR**

```bash
git add scripts/new-dev-certs.ps1 .gitignore docs .claude/skills/external-systems CLAUDE.md src/Infrastructure/CLAUDE.md
git commit -m "docs(integrations): record ADR 0031 and add the external-systems skill"
git push -u origin claude/external-systems
gh pr create --title "feat(integrations): call external systems over mTLS and OAuth 2.0" --body "<summary of tasks 1-10, the probe outcomes, and the Review Focus list from this plan>

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

The PR is done when its seven required checks are green — and Review Focus item 1 means the `backend` jobs specifically. Merging is the user's step.
