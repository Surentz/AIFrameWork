using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure;
using AiFramework.Infrastructure.Caching;
using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Jobs;
using AiFramework.Infrastructure.Jobs.Scheduling;
using AiFramework.Infrastructure.Persistence;
using AiFramework.Infrastructure.Resilience;
using AiFramework.Worker.Observability;
using JasperFx;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    // Same guard, same reason as src/Api/Program.cs: GetConnectionString returns "" rather than
    // null for an unset-but-present key, so a `?? throw` never fires against appsettings.json's
    // "Default": "". This fails at startup instead of deep inside Npgsql on the first job.
    throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
}

builder.AddWorkerObservability();

builder.Services.AddInfrastructure(connectionString);

// The job CLOCK (ADR 0017). Here and only here: the API never starts a scheduler. Quartz fires a
// schedule on exactly one worker and enqueues the job; Wolverine, configured below, runs it.
builder.Services.AddJobScheduling(connectionString);

// The worker's answer to "who is calling". The API binds this to the cookie's claims; here it is
// the job's own OwnerId, set by JobUserMiddleware before the handler runs.
//
// Registered HERE rather than inside AddJobs, and that is load-bearing: AddJobs runs inside
// AddInfrastructure, which every host calls, so binding ICurrentUser there would silently replace
// the API's CurrentUser — last registration wins — and every authenticated request would report
// no caller. Who the caller is, is a host-level decision. See JobRegistration.AddJobs.
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<JobCurrentUser>());

builder.Services.Configure<JobOptions>(builder.Configuration.GetSection("Jobs"));

// Bound here rather than in AddCaching/AddResilience, the same shape the Api uses: each host
// reads its own configuration and hands the values to Infrastructure.
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));
builder.Services.Configure<ResilienceOptions>(builder.Configuration.GetSection("Resilience"));

// .NET's default is 30 seconds, which would abandon a heavy job long before Kubernetes was
// willing to: k8s/base/worker.yaml sets terminationGracePeriodSeconds: 300. The two numbers are
// meaningless apart — raising one without the other either wastes the grace period or gets the
// process SIGKILLed mid-handler, which burns a retry attempt exactly as k8s/base/api.yaml records
// for the outbox.
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromMinutes(5));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AiFrameworkDbContext>();

// Bound straight off configuration rather than resolved from DI, because AddWolverineEventPath
// needs the lane list at CONFIGURATION time: UseWolverine hooks the host builder, which runs
// before any service provider exists. Building a throwaway provider to read it is what ASP0000
// forbids (it would duplicate every singleton), and this is the same shape
// ObservabilityRegistration already uses for ObservabilityOptions.
//
// The Configure<JobOptions> call above still stands for anything resolving IOptions<JobOptions>
// at runtime. An unknown lane name fails either way and earlier here than there: ListenForJobs
// calls ParseQueues during this very call, which throws before the host is built.
var jobOptions = builder.Configuration.GetSection("Jobs").Get<JobOptions>() ?? new JobOptions();

builder.Host.AddWolverineEventPath(
    connectionString,
    // This project's own assembly: Release resolves pre-generated adapters from it, and
    // `codegen write` run against this project writes them into src/Worker/Internal/Generated.
    // It cannot be the Api's — that would need a Worker -> Api reference the dependency rule
    // forbids — which is why there are two generated trees. ADR 0016.
    typeof(Program).Assembly,
    // The whole of the split: this host listens on the lanes in Jobs:Queues and runs the job
    // handlers. The API passes PublishesJobs and listens on nothing.
    role: WolverineHostRole.ProcessesJobs,
    jobOptions: jobOptions,
    durable: builder.Configuration.GetValue("Wolverine:Durable", defaultValue: true));

var app = builder.Build();

// The ONLY two endpoints. No controllers, no MapControllers — this host serves probes and
// consumes queues, and anything else appearing here is a feature that belongs in src/Api.
//
// They exist at all because Kubernetes cannot otherwise probe this process: an exec probe needs a
// shell and the chiseled runtime image has none, the same constraint k8s/base/api.yaml records
// for its preStop hook. /health stays database-free so a liveness probe cannot be failed by
// Postgres being briefly unreachable; /health/ready is the one that may.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapHealthChecks("/health/ready");

// Same split as src/Api/Program.cs, for the same two reasons: `codegen write` is a JasperFx
// command and RunAsync offers no way to invoke one, while JasperFx discovers commands by
// reflecting over every loaded assembly and prints a line per assembly as it goes. With no args
// there is no command to find, so the plain branch keeps an ordinary start quiet.
return args.Length == 0
    ? await RunTheWorkerAsync(app)
    : await app.RunJasperFxCommands(args);

static async Task<int> RunTheWorkerAsync(WebApplication worker)
{
    await worker.RunAsync();
    return 0;
}

/// <summary>Exposed so <c>WebApplicationFactory</c> can find the entry point.</summary>
public partial class Program
{
    // S1118 treats this as a utility-class candidate and asks for a protected constructor or a
    // static class. It cannot be static: WebApplicationFactory<Program> binds Program as a
    // generic type argument, which requires an instantiable reference type.
    protected Program()
    {
    }
}
