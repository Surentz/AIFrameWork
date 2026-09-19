using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace AiFramework.Api.IntegrationTests.Jobs;

/// <summary>
/// The scheduler half of "jobs never run in the API" (ADR 0016, ADR 0017). Piece 5 will give the
/// API a scheduler it never starts, for pause/resume; until then, it has none at all.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class ApiHasNoSchedulerTests(ApiFactory factory)
{
    [Fact]
    public void TheApi_RegistersNoQuartzScheduler()
    {
        factory.Services.GetService<ISchedulerFactory>().Should().BeNull(
            "Quartz is started only in the worker; an API that fires schedules competes with requests");
    }
}
