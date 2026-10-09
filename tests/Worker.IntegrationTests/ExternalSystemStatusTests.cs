using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Monitoring;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Worker.IntegrationTests;

[Collection(nameof(WorkerFactoryCollection))]
public sealed class ExternalSystemStatusTests(WorkerFactory factory)
{
    [Fact]
    public async Task Publisher_WritesTheUnreachableSystemAsUnhealthy()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        ExternalSystemStatusRow? row = null;
        while (row is null && DateTimeOffset.UtcNow < deadline)
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
                row = await context.ExternalSystemStatuses.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.Name == "Unreachable");
            }

            if (row is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }

        row.Should().NotBeNull("the worker's publisher writes every configured system");
        row.State.Should().Be(ExternalSystemState.Unhealthy);
    }
}
