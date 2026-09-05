using AiFramework.Api;
using AiFramework.Infrastructure;
using AiFramework.Infrastructure.EventPath;
using JasperFx;

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
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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
