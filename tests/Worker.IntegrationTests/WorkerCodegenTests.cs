using AiFramework.Infrastructure;
using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiFramework.Worker.IntegrationTests;

/// <summary>
/// The worker's half of ADR 0005's largest ongoing cost, and the reason ADR 0016 calls a second
/// generated tree the biggest concrete price of splitting the host.
///
/// Release runs handler adapters generated ahead of time and committed under
/// src/Worker/Internal/Generated, because it ships without the Roslyn compiler. Add or change a
/// job handler without re-running codegen and Debug stays perfectly green — Debug still compiles
/// adapters at startup — while Release breaks at boot. This opts a Debug run into the Release
/// load path so that gap fails here instead.
///
/// Note this tree is separate from the Api's and must be regenerated separately. It cannot be
/// shared: TypeLoadMode.Static resolves pre-built types out of opts.ApplicationAssembly, and
/// pointing this host at the Api's assembly would need a Worker -> Api reference the dependency
/// rule forbids.
/// </summary>
public sealed class WorkerCodegenTests
{
    // No database is touched: durable is false, so this is MediatorOnly and never connects. The
    // value only has to satisfy the guard at the top of the worker's Program.cs.
    private const string PlaceholderConnectionString =
        "Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder";

    [Fact]
    public async Task TheCommittedGeneratedCode_CoversEveryJobHandler()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddInfrastructure(PlaceholderConnectionString))
            .AddWolverineEventPath(
                PlaceholderConnectionString,
                // The Worker assembly, because that is where this project's codegen writes and
                // where the generated files are compiled in — the same assembly Program.cs passes.
                typeof(Program).Assembly,
                // ProcessesJobs, so the job handlers are discovered and their adapters are the
                // thing being checked. PublishesJobs would discover none and pass vacuously.
                role: WolverineHostRole.ProcessesJobs,
                jobOptions: new JobOptions { Queues = string.Empty },
                durable: false,
                // The point of the test. In Release this is the default; forcing it here makes a
                // stale-codegen failure surface in the Debug suite everyone actually runs.
                usePreGeneratedCode: true)
            .Build();

        var start = async () =>
        {
            await host.StartAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);
        };

        await start.Should().NotThrowAsync(
            "the generated code under src/Worker/Internal/Generated must match the current " +
            "handlers — regenerate it with `dotnet run --project src/Worker -- codegen write`");
    }
}
