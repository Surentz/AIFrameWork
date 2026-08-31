using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Tests.Messaging;

public sealed record Ping(string Text) : ICommand<string>;

public sealed class PingHandler : ICommandHandler<Ping, string>
{
    public Task<Result<string>> HandleAsync(Ping command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult(Result.Success(command.Text.ToUpperInvariant()));
    }
}

public sealed class CommandDispatcherTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddCommand<Ping, string, PingHandler>();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SendAsync_WithARegisteredCommand_InvokesItsHandler()
    {
        await using var provider = BuildProvider();
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        var result = await dispatcher.SendAsync(new Ping("hello"), CancellationToken.None);

        result.Value.Should().Be("HELLO");
    }

    [Fact]
    public async Task SendAsync_WithAnUnregisteredCommand_Throws()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        var act = async () => await dispatcher.SendAsync(new Ping("hello"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ping*");
    }
}
