using AiFramework.Api;
using AiFramework.Infrastructure;
using AiFramework.Infrastructure.EventPath;

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
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddInfrastructure(connectionString);

// ADR 0005 spike: Wolverine's durable event path, alongside the existing outbox rather than
// replacing it. UseWolverine hooks the host builder, so this cannot go through AddInfrastructure.
builder.Host.AddWolverineEventPath(
    connectionString,
    // Durable Wolverine connects to Postgres while the host starts, so a host with no reachable
    // database no longer boots. Configurable so a test that deliberately runs without one
    // (HealthTests) can still start the app. Defaults to durable everywhere else.
    durable: builder.Configuration.GetValue("Wolverine:Durable", defaultValue: true));

var app = builder.Build();

app.UseExceptionHandler();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

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
