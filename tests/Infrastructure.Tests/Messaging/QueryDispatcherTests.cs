using AiFramework.Application.Abstractions;
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
    [Fact]
    public async Task SendAsync_WithARegisteredQuery_InvokesItsHandler()
    {
        var services = new ServiceCollection();
        services.AddQuery<Echo, int, EchoHandler>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var result = await dispatcher.SendAsync(new Echo(21), CancellationToken.None);

        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task SendAsync_WithAnUnregisteredQuery_Throws()
    {
        var services = new ServiceCollection();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var act = async () => await dispatcher.SendAsync(new Echo(1), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Echo*");
    }
}
