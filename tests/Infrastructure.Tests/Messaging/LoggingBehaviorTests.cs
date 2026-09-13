using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.Messaging;

public sealed record Announce(string Text) : ICommand<string>;

public sealed class AnnounceHandler : ICommandHandler<Announce, string>
{
    public Task<Result<string>> HandleAsync(Announce command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult(Result.Success(command.Text));
    }
}

/// <summary>Fails with a caller-supplied ErrorKind, so a test can drive Behaviors.LevelFor.</summary>
public sealed record Refuse(ErrorKind Kind) : ICommand<string>;

public sealed class RefuseHandler : ICommandHandler<Refuse, string>
{
    public Task<Result<string>> HandleAsync(Refuse command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult(Result.Failure<string>(new Error(command.Kind, "refuse.failed", "no")));
    }
}

public sealed record Detonate(string Text) : ICommand<string>;

public sealed class DetonateHandler : ICommandHandler<Detonate, string>
{
    public Task<Result<string>> HandleAsync(Detonate command, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("boom");
}

/// <summary>
/// Behaviors.LoggedAsync wraps every command and query dispatch (MessagingRegistration.AddCommand/
/// AddQuery), so these tests exercise it the same way the rest of this directory exercises the
/// other behaviors: through a real ICommandDispatcher, not by calling LoggedAsync directly.
/// </summary>
public sealed class LoggingBehaviorTests
{
    private static (ServiceProvider Provider, CapturingLoggerProvider Logs) Build<TCommand, TResponse, THandler>()
        where TCommand : ICommand<TResponse>
        where THandler : class, ICommandHandler<TCommand, TResponse>
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();

        // SetMinimumLevel(Debug) is deliberate, not incidental: Microsoft.Extensions.Logging
        // defaults LoggerFilterOptions.MinLevel to Information when nothing configures it —
        // the same default appsettings.json ships in production — so MessagingLog.Succeeded and
        // a Validation/NotFound MessagingLog.Failed (both Debug) would never reach even a
        // provider whose own IsEnabled always returns true. These tests exist to prove that
        // Debug-level record is actually produced, which needs the filter turned down to see it.
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(logs);
        });
        services.AddCommand<TCommand, TResponse, THandler>();
        services.AddSingleton<CommandRegistry>();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddSingleton(Substitute.For<IUnitOfWork>());

        return (services.BuildServiceProvider(), logs);
    }

    [Fact]
    public async Task SendAsync_OnSuccess_LogsAtDebug()
    {
        var (provider, logs) = Build<Announce, string, AnnounceHandler>();
        await using var _ = provider;
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        await dispatcher.SendAsync(new Announce("hello"), CancellationToken.None);

        logs.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Debug
            && r.Message.Contains("Announce", StringComparison.Ordinal)
            && r.Message.Contains("succeeded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ErrorKind.Validation, LogLevel.Debug)]
    [InlineData(ErrorKind.NotFound, LogLevel.Debug)]
    [InlineData(ErrorKind.Conflict, LogLevel.Information)]
    [InlineData(ErrorKind.Unauthorized, LogLevel.Information)]
    public async Task SendAsync_OnAFailedResult_LogsAtTheLevelItsErrorKindMapsTo(
        ErrorKind kind, LogLevel expected)
    {
        var (provider, logs) = Build<Refuse, string, RefuseHandler>();
        await using var _ = provider;
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        await dispatcher.SendAsync(new Refuse(kind), CancellationToken.None);

        logs.Records.Should().ContainSingle(r =>
            r.Level == expected
            && r.Message.Contains("Refuse", StringComparison.Ordinal)
            && r.Message.Contains("refuse.failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendAsync_WhenTheHandlerThrows_LogsFaultedAtWarningAndRethrows()
    {
        var (provider, logs) = Build<Detonate, string, DetonateHandler>();
        await using var _ = provider;
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        var act = async () =>
            await dispatcher.SendAsync(new Detonate("x"), CancellationToken.None);

        // GlobalExceptionHandler is what turns this into a 500 at the HTTP boundary and logs the
        // exception itself — LoggedAsync must let it propagate, not swallow it (CA1031 forbids a
        // catch here regardless).
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        logs.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Warning
            && r.Message.Contains("Detonate", StringComparison.Ordinal)
            && r.Message.Contains("threw", StringComparison.Ordinal));

        // Exactly one record, not two: LoggedAsync must not ALSO log a Succeeded/Failed record
        // for a dispatch that never returned a Result.
        logs.Records.Should().HaveCount(1);
    }
}

/// <summary>A minimal ILoggerProvider that captures every rendered record, for asserting on
/// what Behaviors.LoggedAsync actually wrote — not on whether it called some method.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<CapturedLogRecord> _records = [];

    public IReadOnlyList<CapturedLogRecord> Records => _records;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            owner._records.Add(new CapturedLogRecord(logLevel, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

public sealed record CapturedLogRecord(LogLevel Level, string Message);
