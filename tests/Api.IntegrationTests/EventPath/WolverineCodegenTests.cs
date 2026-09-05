using AiFramework.Infrastructure.EventPath;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiFramework.Api.IntegrationTests.EventPath;

/// <summary>
/// Guards the one ongoing cost ADR 0005 calls its largest: Release runs handler adapters that
/// were generated ahead of time and committed, because it ships without the Roslyn compiler
/// (Debug-only package reference; it costs 33MB — measured, 17MB to 50MB published). Add or
/// change a Wolverine handler without re-running codegen and Debug stays perfectly green,
/// because Debug still compiles adapters at startup — only Release breaks, at boot.
///
/// This test closes that gap by opting a Debug run into the Release load path.
/// </summary>
public sealed class WolverineCodegenTests
{
    // No database is touched: durable is false, so this is MediatorOnly and never connects.
    // The value only has to satisfy the guard at the top of Program.cs.
    private const string PlaceholderConnectionString =
        "Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder";

    [Fact]
    public async Task TheCommittedGeneratedCode_CoversEveryRegisteredHandler()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddWolverineEventPathServices())
            .AddWolverineEventPath(
                PlaceholderConnectionString,
                // The Api assembly, because that is where codegen writes and where the
                // generated files are compiled in — the same assembly Program.cs passes.
                typeof(Program).Assembly,
                durable: false,
                // The point of the test. In Release this is the default; forcing it here makes
                // a stale-codegen failure surface in the Debug suite everyone actually runs.
                usePreGeneratedCode: true)
            .Build();

        var start = async () =>
        {
            await host.StartAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);
        };

        await start.Should().NotThrowAsync(
            "the generated code under src/Api/Internal/Generated must match the current " +
            "handlers — regenerate it with `dotnet run --project src/Api -- codegen write`");
    }
}
