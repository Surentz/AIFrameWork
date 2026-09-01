using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.Messaging;

public sealed record Save(string Name) : ICommand<string>;

public sealed class SaveValidator : AbstractValidator<Save>
{
    public SaveValidator() => RuleFor(c => c.Name).NotEmpty();
}

public sealed class SaveHandler : ICommandHandler<Save, string>
{
    public bool WasCalled { get; private set; }

    public Task<Result<string>> HandleAsync(Save command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        WasCalled = true;
        return Task.FromResult(Result.Success(command.Name));
    }
}

public sealed class FailingHandler : ICommandHandler<Save, string>
{
    public Task<Result<string>> HandleAsync(Save command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult(Result.Failure<string>(
            new Error(ErrorKind.Conflict, "conflict", "nope")));
    }
}

public sealed class BehaviorTests
{
    private static ServiceProvider Build<THandler>(IUnitOfWork unitOfWork, bool withValidator)
        where THandler : class, ICommandHandler<Save, string>
    {
        var services = new ServiceCollection();
        services.AddCommand<Save, string, THandler>();
        services.AddSingleton<CommandRegistry>();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddSingleton(unitOfWork);
        if (withValidator)
        {
            services.AddScoped<IValidator<Save>, SaveValidator>();
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SendAsync_WhenValidationFails_ReturnsValidationError()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        await using var provider = Build<SaveHandler>(unitOfWork, withValidator: true);
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        var result = await dispatcher.SendAsync(new Save(""), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task SendAsync_WhenValidationFails_DoesNotInvokeTheHandler()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        await using var provider = Build<SaveHandler>(unitOfWork, withValidator: true);
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();
        var handler = (SaveHandler)provider.GetRequiredService<ICommandHandler<Save, string>>();

        await dispatcher.SendAsync(new Save(""), CancellationToken.None);

        handler.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_WhenValidationFails_DoesNotSaveChanges()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        await using var provider = Build<SaveHandler>(unitOfWork, withValidator: true);
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        await dispatcher.SendAsync(new Save(""), CancellationToken.None);

        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_OnSuccess_SavesChangesExactlyOnce()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        await using var provider = Build<SaveHandler>(unitOfWork, withValidator: true);
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        await dispatcher.SendAsync(new Save("ok"), CancellationToken.None);

        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WhenTheHandlerFails_DoesNotSaveChanges()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        await using var provider = Build<FailingHandler>(unitOfWork, withValidator: true);
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        await dispatcher.SendAsync(new Save("ok"), CancellationToken.None);

        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithNoValidatorRegistered_StillRunsTheHandler()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        await using var provider = Build<SaveHandler>(unitOfWork, withValidator: false);
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();
        var handler = (SaveHandler)provider.GetRequiredService<ICommandHandler<Save, string>>();

        var result = await dispatcher.SendAsync(new Save(""), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        handler.WasCalled.Should().BeTrue();
    }
}
