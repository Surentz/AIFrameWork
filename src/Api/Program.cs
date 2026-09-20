using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AiFramework.Api;
using AiFramework.Api.Auth;
using AiFramework.Api.Notifications;
using AiFramework.Api.Observability;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Notifications;
using AiFramework.Application.Users;
using AiFramework.Infrastructure;
using AiFramework.Infrastructure.Caching;
using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Persistence;
using AiFramework.Infrastructure.Resilience;
using JasperFx;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    // GetConnectionString returns "" for an unset-but-present key, not null, so a
    // `?? throw` guard never fires against appsettings.json's "Default": "". Checking for
    // whitespace as well as null is what makes this fail at startup instead of deep inside
    // Npgsql on the first request.
    throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
}

// Registered before everything else so the tracer provider exists for the whole pipeline —
// including the auth handler's OnRedirectToLogin/OnValidatePrincipal callbacks below, which run
// inside a request's own Activity. Export is configuration-gated and off by default; the tracer
// provider itself is not, because it is what makes Activity.Current non-null, which is what
// makes the traceId already written into every ProblemDetails below resolve to something real.
// See docs/superpowers/plans/2026-09-13-centralized-logging.md and ADR 0014.
builder.AddObservability();

builder.Services.AddControllers()
    // Enums cross the wire as their NAMES. Without this, System.Text.Json writes the underlying
    // integer, the OpenAPI document describes the property as a bare int, and
    // frontend/src/api/schema.d.ts types it as `number` - so adding a member, or reordering the
    // ones that exist, changes what every existing value means with nothing to catch it. As
    // names, each enum reaches TypeScript as a string union, and a client switching over it
    // fails to compile when a member is added rather than falling through at runtime.
    //
    // Safe to adopt globally at this point precisely because no endpoint exposed an enum before
    // NotificationKind and OrderStatus: the generated contract carried no enum schema at all, so
    // this changes no existing response shape (verified against openapi/AiFramework.Api.json).
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// The SAME converter again, on a DIFFERENT options type, and both calls are required. .NET has
// two unrelated JsonOptions: Microsoft.AspNetCore.Mvc.JsonOptions (above) is what controllers
// actually serialize with, and Microsoft.AspNetCore.Http.Json.JsonOptions (here) is what
// AddOpenApi's schema generator reads. Configuring only the first is the trap: the API sends
// "OrderPlaced" at runtime while the generated contract still declares the property an integer,
// so frontend/src/api/schema.d.ts types it `number` and every client is wrong in a way no
// backend test can see. Caught exactly that way - the integration test asserting
// "kind":"OrderPlaced" passed while openapi/AiFramework.Api.json said type: integer.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Cookie, not a bearer token: the SPA is served same-origin (vite.config.ts proxies /api), so
// the browser attaches this by itself and no JavaScript ever holds the session - an XSS bug has
// nothing to steal. It also means no signing key, which this repo could not put in
// appsettings.json anyway: the no-secrets hook blocks a bare "Key" for exactly that reason.
// ADR 0006.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "aiframework.session";
        options.Cookie.HttpOnly = true;

        // Lax is the CSRF defence: it withholds the cookie on a cross-site POST, and every
        // endpoint here binds JSON, which a cross-site HTML form cannot send. No antiforgery
        // token is issued on top of that.
        options.Cookie.SameSite = SameSiteMode.Lax;

        // SameAsRequest in Development only: the dev API is plain HTTP on 5234, and a Secure
        // cookie there would be set by some browsers and silently dropped by others.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;

        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;

        // This is an API, not a server-rendered app. Left alone, the cookie handler answers an
        // unauthenticated request with a 302 to a login page that does not exist here, so a
        // fetch() would see a 404 of HTML instead of a 401 and the client could not tell "signed
        // out" from "broken".
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };

        // The stamp check, and the reason ADR 0011's invalidation works at all. Runs on every request
        // that presents a cookie, before the endpoint sees it, and is what makes a password change, a
        // lockout, or a sign-out-everywhere take effect on the next request instead of whenever the
        // cookie happens to expire.
        //
        // Rejecting rather than throwing: a stale session is an expected state, not a fault. The
        // principal is dropped and the cookie cleared, so the request continues unauthenticated and
        // lands on OnRedirectToLogin above - coming back as a 401, exactly as a request with no cookie
        // would. The client cannot tell the two apart, which is the point.
        options.Events.OnValidatePrincipal = async context =>
        {
            var principal = context.Principal;

            if (principal?.Identity?.IsAuthenticated != true)
            {
                return;
            }

            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var stamp = principal.FindFirstValue(SessionClaims.SecurityStamp);

            if (!Guid.TryParse(userId, out var id) || string.IsNullOrEmpty(stamp))
            {
                // No stamp claim means a cookie minted before ADR 0011. Failing closed retires those on
                // their holder's next request rather than trusting them until they expire.
                await RejectAsync(context).ConfigureAwait(false);
                return;
            }

            // Resolved per request, from the request's own scope: ISessionValidator is scoped and holds
            // the scoped DbContext. Capturing it outside this lambda would leak one DbContext across
            // every request in the process.
            var validator = context.HttpContext.RequestServices.GetRequiredService<ISessionValidator>();

            if (!await validator
                    .IsStampCurrentAsync(id, stamp, context.HttpContext.RequestAborted)
                    .ConfigureAwait(false))
            {
                await RejectAsync(context).ConfigureAwait(false);
            }
        };
    });

builder.Services.AddAuthorization();

// The caller, as an Application port. HttpContextAccessor is what makes the claims reachable
// from a handler; scoped because "who is calling" is per-request.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// Volume defence on the credential endpoints, alongside the per-account lockout in
// SignInHandler. Partitioned by remote address, not by username: the limiter runs before model
// binding, so a username in the JSON body is not reachable without buffering and rewinding the
// request body. Counting clients rather than accounts also means this 429 distinguishes nothing
// about which accounts exist - which is why the lockout itself stays silent. ADR 0008.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    var permitLimit = builder.Configuration.GetValue("RateLimiting:Auth:PermitLimit", defaultValue: 10);
    var windowSeconds = builder.Configuration.GetValue("RateLimiting:Auth:WindowSeconds", defaultValue: 60);

    options.AddPolicy(AuthRateLimiting.PolicyName, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Null for a request with no remote address (in-memory test hosts, some proxies).
            // One shared "unknown" bucket is the safe direction: it over-restricts rather than
            // handing every such caller its own unlimited partition.
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
            }));

    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            // Ceiling, not a cast: truncation turns the 0.4s left in a window into
            // "Retry-After: 0", and a well-behaved client obeys it straight back into another 429.
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        // The limiter short-circuits the pipeline, so nothing else gives this response a body:
        // AddProblemDetails() only fills one in for an exception, and there is no
        // UseStatusCodePages here. Without this the 429 goes out empty while the committed
        // contract - openapi/AiFramework.Api.json and the schema.d.ts generated from it - says it
        // returns ProblemDetails, and frontend/src/api/client.ts falls back to its own
        // "Request failed with status 429." Shaped exactly like ResultExtensions.Problem's
        // output, traceId included, so an error body from here is not a special case for a client.
        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "rate.limited",
            Detail = "Too many requests. Please wait a moment and try again.",
        };
        problemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;

        await context.HttpContext.Response.WriteAsJsonAsync(
            problemDetails,
            options: null,
            contentType: "application/problem+json",
            cancellationToken);
    };
});

// Off by default, and that default is load-bearing. The limiter above partitions on
// Connection.RemoteIpAddress, which behind ingress-nginx is the ingress pod for every caller -
// so the whole world shares one partition. Believing X-Forwarded-For fixes that, but only by
// clearing the proxy allow-list below, and a host that trusts the header while being directly
// reachable lets any caller mint a fresh partition per request simply by varying it. Enable it
// only where an ingress is provably the sole path in; see ADR 0012.
if (builder.Configuration.GetValue("ForwardedHeaders:Enabled", defaultValue: false))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        // XForwardedFor only. Nothing in this application reads Request.IsHttps -
        // CookieSecurePolicy.Always is unconditional in Production - so forwarding the proto
        // would change behaviour for no benefit.
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;

        // Exactly one proxy: the ingress. A larger limit would let a client prepend its own
        // hop and choose which address the limiter sees.
        options.ForwardLimit = 1;

        // Cleared because the proxy is a cluster-assigned pod IP, not loopback, and its address
        // is not knowable at build time. Safe only under the flag above.
        // The network list property was renamed to KnownIPNetworks in this SDK (ASPDEPR005
        // flags the old name as obsolete); the proxy list property has no such replacement.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(connectionString);

// The key ring goes to Postgres, not to each host's memory. Two replicas with separate
// rings reject each other's session cookies, which surfaces as an intermittent 401 rather
// than an obvious failure. SetApplicationName is load-bearing, not decoration: the purpose
// string derives from it, so pods that disagree on the name share a ring and still refuse
// each other's cookies. Keys are unencrypted at rest - acceptable for a local cluster with
// throwaway credentials, and the condition ADR 0010 places on a cloud target.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AiFrameworkDbContext>()
    .SetApplicationName("AiFramework");

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AiFrameworkDbContext>();

// Bound here rather than inside AddCaching, which must stay resolvable from a bare
// ServiceCollection in unit tests. Same shape as Wolverine:Durable and RateLimiting:Auth above:
// Api reads its own configuration and hands the values to Infrastructure.
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));

// Realtime notification push. OFF by default (see RealtimeOptions), so nothing below runs for a
// developer who has started no Redis, or in CI - the REST feed is the source of truth either way
// and a client that receives no push simply polls. ADR 0019.
var realtimeSection = builder.Configuration.GetSection("Realtime");
builder.Services.Configure<RealtimeOptions>(realtimeSection);
var realtime = realtimeSection.Get<RealtimeOptions>() ?? new RealtimeOptions();
string? realtimeWarning = null;

if (realtime.Enabled)
{
    // A THIRD JsonSerializerOptions, and it is not reached by either of the two above.
    // JsonHubProtocolOptions.PayloadSerializerOptions is what SignalR serializes hub payloads
    // with; AddJsonOptions covers controllers and ConfigureHttpJsonOptions covers the OpenAPI
    // generator, and neither touches this one. Without this line a push sends "kind":0 while
    // GET /api/notifications sends "kind":"OrderPlaced" — two wire contracts for one concept,
    // only one of which is in schema.d.ts. Proved by NotificationPushPayloadTests, which
    // asserts against the host's own resolved options rather than a hand-built copy.
    var signalR = builder.Services.AddSignalR()
        .AddJsonProtocol(options =>
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    if (!string.IsNullOrWhiteSpace(realtime.RedisConnectionString))
    {
        // What makes a push reach a user connected to the OTHER replica. The pod that writes a
        // notification is whichever one's outbox pump claimed the row, and that is unrelated to
        // the pod holding the connection - the ingress cookie affinity of ADR 0010 does not help
        // here, because the pump is not serving that user's request.
        signalR.AddStackExchangeRedis(realtime.RedisConnectionString);
    }
    else
    {
        // Legitimate at one replica, silently wrong above it - so it is said out loud once, at
        // startup, rather than discovered as "realtime works for some people". Logged after
        // Build(), which is the first point an ILogger exists.
        realtimeWarning =
            "Realtime push is enabled with no Redis backplane. That is correct for a single "
            + "instance only: with more than one replica, a push reaches a user only when the "
            + "pod that wrote the notification is also the one holding their connection. "
            + "Set Realtime__RedisConnectionString. See ADR 0019.";
    }

    // The port Application's notifiers depend on, as IEnumerable<INotificationPush> - registered
    // ONLY when realtime is on, so "off" is an empty sequence rather than a fake implementation
    // that looks like it works. See INotificationPush's own remarks.
    builder.Services.AddSingleton<INotificationPush, SignalRNotificationPush>();
}

// Same reason, same shape: AddResilience registers and validates ResilienceOptions but does not
// bind, so it stays resolvable from a bare ServiceCollection in a unit test. Api reads its own
// configuration and hands the values to Infrastructure. ADR 0014.
builder.Services.Configure<ResilienceOptions>(builder.Configuration.GetSection("Resilience"));

// ADR 0005 spike: Wolverine's durable event path, alongside the existing outbox rather than
// replacing it. UseWolverine hooks the host builder, so this cannot go through AddInfrastructure.
builder.Host.AddWolverineEventPath(
    connectionString,
    // Wolverine loads its pre-generated Release adapters from this assembly, and codegen
    // writes them into this project. typeof(Program) rather than GetEntryAssembly() so that
    // WebApplicationFactory tests resolve the Api assembly and not the test host.
    typeof(Program).Assembly,
    // The API publishes jobs and listens on no job queue — the whole of ADR 0016's split. No
    // JobOptions is passed because there is nothing to listen on; a worker must pass one.
    // ApiPublishesOnlyTests asserts this against the runtime's own endpoint list.
    role: WolverineHostRole.PublishesJobs,
    // Durable Wolverine connects to Postgres while the host starts, so a host with no reachable
    // database no longer boots. Configurable so a test that deliberately runs without one
    // (HealthTests) can still start the app. Defaults to durable everywhere else.
    durable: builder.Configuration.GetValue("Wolverine:Durable", defaultValue: true));

var app = builder.Build();

if (realtimeWarning is not null)
{
#pragma warning disable CA1848 // One-off startup log, not a hot path; a [LoggerMessage] partial needs a class to live on and Program.cs is top-level statements.
    app.Logger.LogWarning("{Warning}", realtimeWarning);
#pragma warning restore CA1848
}

// First, conventionally: everything downstream - exception logging, authentication, the rate
// limiter - should see the client's real address rather than the ingress's. The only *live*
// constraint today is that this precedes UseRateLimiter, since the limiter partitions on the
// connection's address and a rewrite after it reads has no effect - but placing it first means a
// later addition (request logging, an IP-based policy) inherits the right address by
// construction instead of by remembering this comment. Registered unconditionally - with the
// flag off, ForwardedHeadersOptions keeps its defaults, which forward nothing.
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

// Only when realtime is on - mapping a hub whose INotificationPush was never registered would
// accept connections that can never receive anything.
if (realtime.Enabled)
{
    app.MapHub<NotificationHub>("/hubs/notifications");
}

// No fallback authorization policy is registered, so this stays anonymous without an attribute -
// a readiness probe that needs credentials is not a readiness probe.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Readiness, distinct from the liveness check above: this one answers "may this pod receive
// traffic", which needs Postgres. It stays a separate endpoint because /health must remain
// database-free — HealthTests boots a host with no database at all and asserts 200 on it.
// Anonymous for the same reason /health is: no fallback authorization policy is registered.
app.MapHealthChecks("/health/ready");

// Development only, deliberately: a deployed instance must not publish its endpoint surface.
// Asserted in both directions by OpenApiDocumentTests, because a missing environment check
// here would otherwise ship silently.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Wolverine generates its handler adapters with Roslyn, and Release deliberately ships without
// the compiler (see the Debug-only package reference in AiFramework.Infrastructure.csproj - it
// costs 33MB, measured: 17MB -> 50MB published). Release therefore runs pre-generated code, and
// RunJasperFxCommands is what makes the command that generates it reachable:
//
//     dotnet run --project src/Api -- codegen write
//
// Only when a command is actually named, though. RunJasperFxCommands discovers commands by
// reflecting over every loaded assembly and announces each one it scans ("Searching 'Wolverine,
// Version=...' for commands"), six lines of noise before every ordinary startup. With no args
// there is no command to find, so plain RunAsync costs nothing and keeps `dotnet run` and F5
// quiet.
//
// This does NOT make WebApplicationFactory take the RunAsync branch: it passes args of its own,
// so the tests still go through JasperFx and still depend on
// tests/Api.IntegrationTests/JasperFxTestEnvironment.cs setting AutoStartHost. Verified by
// removing that module initializer, which fails 23 of the 24 integration tests.
//
// ADR 0005.
return args.Length == 0
    ? await RunTheWebApplicationAsync(app)
    : await app.RunJasperFxCommands(args);

// A local function purely so both branches are expression-bodied and return int; RunAsync
// itself returns Task. Zero is the conventional "exited cleanly" code JasperFx also returns.
static async Task<int> RunTheWebApplicationAsync(WebApplication webApplication)
{
    await webApplication.RunAsync();
    return 0;
}

// Both rejection paths do the same two things, and doing only the first leaves the dead cookie
// in the browser to be re-sent and re-rejected on every subsequent request.
//
// Not async/await: RejectPrincipal() is synchronous and SignOutAsync is the only await, so
// AsyncFixer01 asks for the task to be returned directly rather than wrapped in a state machine.
// The caller still applies ConfigureAwait(false) when it awaits this.
static Task RejectAsync(CookieValidatePrincipalContext context)
{
    context.RejectPrincipal();

    return context.HttpContext
        .SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
}

/// <summary>Exposed so <c>WebApplicationFactory</c> can find the entry point.</summary>
public partial class Program
{
    // SonarAnalyzer S1118 treats this as a utility-class candidate (no instance members)
    // and asks for either a protected constructor or a static class. It cannot be static:
    // WebApplicationFactory<Program> binds Program as a generic type argument, which
    // requires an instantiable reference type. A protected constructor satisfies the
    // analyzer without blocking that usage — WebApplicationFactory never calls `new Program()`.
    protected Program()
    {
    }
}
