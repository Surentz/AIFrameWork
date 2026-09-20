using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.Messaging;

/// <summary>
/// Behaviors.LoggedAsync must never log a request instance, only its type name — see the
/// remarks on LoggedAsync itself. SignIn, RegisterUser and ChangePassword are records carrying
/// a plaintext password field, so a future edit that "improves" logging by capturing the request
/// object (its ToString(), its properties as structured state, anything) would write every
/// password in the system to the log store, permanently. This is the test that catches that
/// class of regression — the same role SensitiveCommandLoggingTests's caching counterpart plays
/// for a cache key missing its user scope (ADR 0009): a mistake ordinary review would not
/// reliably catch, caught by a test instead.
/// </summary>
public sealed class SensitiveCommandLoggingTests
{
    private const string SentinelPassword = "S3ntinel-P@ssw0rd-that-must-never-appear-in-a-log";

    [Fact]
    public async Task SignIn_WithAnUnknownUsername_NeverLogsThePasswordAnywhere()
    {
        using var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddCommand<SignIn, SessionView, SignInHandler>();
        services.AddSingleton<CommandRegistry>();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<IValidator<SignIn>, SignInValidator>();

        // Unconfigured, so GetByNormalizedUsernameAsync returns null — SignInHandler's
        // unknown-username branch, which still hashes the supplied password before failing
        // (timing-attack defence: see SignIn.cs). That hash-and-discard is exactly the kind of
        // call whose argument a naive logging "improvement" would be tempted to capture.
        services.AddSingleton(Substitute.For<IUserRepository>());
        services.AddSingleton(Substitute.For<IPasswordHasher>());

        // The sign-in audit records the attempted USERNAME and never the password — which is the
        // property this class exists to defend, now that there is a second place a credential
        // could leak. Substituted so nothing is written; the assertion below covers the log.
        services.AddSingleton(Substitute.For<ISignInAudit>());
        services.AddSingleton<IClock>(new TestClock(DateTimeOffset.UtcNow));

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        var result = await dispatcher.SendAsync(
            new SignIn("someone", SentinelPassword), CancellationToken.None);

        result.IsSuccess.Should().BeFalse("there is no such user, so this test proves nothing " +
            "if the command unexpectedly succeeds");

        logs.Records.Should().NotBeEmpty(
            "the point of this test is that logging happened and still carried no secret");

        var leaked = logs.Records.Where(r =>
            r.Message.Contains(SentinelPassword, StringComparison.Ordinal));

        leaked.Should().BeEmpty(
            "Behaviors.LoggedAsync logs typeof(TRequest).Name, never the request instance — a " +
            "record here means something started logging the SignIn command itself");
    }
}
