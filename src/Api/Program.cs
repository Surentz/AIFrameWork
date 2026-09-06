using AiFramework.Api;
using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure;
using AiFramework.Infrastructure.EventPath;
using JasperFx;
using Microsoft.AspNetCore.Authentication.Cookies;
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

builder.Services.AddControllers();

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
    });

builder.Services.AddAuthorization();

// The caller, as an Application port. HttpContextAccessor is what makes the claims reachable
// from a handler; scoped because "who is calling" is per-request.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(connectionString);

// ADR 0005 spike: Wolverine's durable event path, alongside the existing outbox rather than
// replacing it. UseWolverine hooks the host builder, so this cannot go through AddInfrastructure.
builder.Host.AddWolverineEventPath(
    connectionString,
    // Wolverine loads its pre-generated Release adapters from this assembly, and codegen
    // writes them into this project. typeof(Program) rather than GetEntryAssembly() so that
    // WebApplicationFactory tests resolve the Api assembly and not the test host.
    typeof(Program).Assembly,
    // Durable Wolverine connects to Postgres while the host starts, so a host with no reachable
    // database no longer boots. Configurable so a test that deliberately runs without one
    // (HealthTests) can still start the app. Defaults to durable everywhere else.
    durable: builder.Configuration.GetValue("Wolverine:Durable", defaultValue: true));

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// No fallback authorization policy is registered, so this stays anonymous without an attribute -
// a readiness probe that needs credentials is not a readiness probe.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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
