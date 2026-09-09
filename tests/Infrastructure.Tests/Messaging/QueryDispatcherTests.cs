using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Caching;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Tests.Messaging;

public sealed record Echo(int Value) : IQuery<int>;

public sealed class EchoHandler : IQueryHandler<Echo, int>
{
    public Task<Result<int>> HandleAsync(Echo query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Task.FromResult(Result.Success(query.Value * 2));
    }
}

public sealed class QueryDispatcherTests
{
    // AddCaching and AddLogging are what CachedAsync needs to resolve: it reads
    // IOptions<CacheOptions> on every query, and HybridCache's default implementation takes an
    // ILogger. Echo is not ICacheable, so nothing here is actually cached — these registrations
    // exist so the pipeline can run at all.
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        services.AddSingleton<QueryRegistry>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();
        return services;
    }

    [Fact]
    public async Task SendAsync_WithARegisteredQuery_InvokesItsHandler()
    {
        var services = Services();
        services.AddQuery<Echo, int, EchoHandler>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var result = await dispatcher.SendAsync(new Echo(21), CancellationToken.None);

        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task SendAsync_WithAnUnregisteredQuery_Throws()
    {
        var services = Services();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var act = async () => await dispatcher.SendAsync(new Echo(1), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Echo*");
    }

    [Fact]
    public void Construction_WithAQueryRegisteredTwice_ThrowsNamingTheQuery()
    {
        var services = Services();
        services.AddQuery<Echo, int, EchoHandler>();
        services.AddQuery<Echo, int, EchoHandler>();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IQueryDispatcher>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Echo*");
    }
}
