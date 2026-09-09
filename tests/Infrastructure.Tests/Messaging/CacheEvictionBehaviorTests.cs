using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Caching;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.Messaging;

public sealed record Touch(int Value) : ICommand<bool>, IInvalidatesCache
{
    public IReadOnlyList<string> Tags => [nameof(Lookup)];
}

public sealed class TouchHandler : ICommandHandler<Touch, bool>
{
    public Task<Result<bool>> HandleAsync(Touch command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult(Result.Success(true));
    }
}

public sealed class FailingTouchHandler : ICommandHandler<Touch, bool>
{
    public Task<Result<bool>> HandleAsync(Touch command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult(Result.Failure<bool>(
            new Error(ErrorKind.Conflict, "touch.conflict", "nope")));
    }
}

/// <summary>A command that invalidates nothing, to prove eviction is opt-in too.</summary>
public sealed record Quiet(int Value) : ICommand<bool>;

public sealed class QuietHandler : ICommandHandler<Quiet, bool>
{
    public Task<Result<bool>> HandleAsync(Quiet command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult(Result.Success(true));
    }
}

/// <summary>
/// Eviction is tested against the real cache and through the real read path: these tests write an
/// entry by dispatching Lookup, then dispatch Touch, then dispatch Lookup again and count
/// handler calls. Asserting on a mocked RemoveByTagAsync would only prove the behavior calls a
/// method — not that the tag it passes matches the key the query wrote, which is the one thing
/// that can silently drift.
/// </summary>
[Collection(nameof(CacheBehaviorCollection))]
public sealed class CacheEvictionBehaviorTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ServiceProvider Build<TCommandHandler>(
        ICurrentUser currentUser, bool enabled = true)
        where TCommandHandler : class, ICommandHandler<Touch, bool>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        services.Configure<CacheOptions>(o => o.Enabled = enabled);
        services.AddQuery<Lookup, string, CountingLookupHandler>();
        services.AddCommand<Touch, bool, TCommandHandler>();
        services.AddCommand<Quiet, bool, QuietHandler>();
        services.AddSingleton<QueryRegistry>();
        services.AddSingleton<CommandRegistry>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddSingleton(currentUser);
        services.AddSingleton(Substitute.For<IUnitOfWork>());

        return services.BuildServiceProvider();
    }

    private static ICurrentUser UserOf(Guid? id)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(id);
        return currentUser;
    }

    [Fact]
    public async Task SendAsync_AfterASuccessfulInvalidatingCommand_TheNextReadIsAMiss()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<TouchHandler>(UserOf(Alice));
        var queries = provider.GetRequiredService<IQueryDispatcher>();
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        await queries.SendAsync(new Lookup(1), CancellationToken.None);
        await commands.SendAsync(new Touch(1), CancellationToken.None);
        await queries.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(
            2,
            "the tag the command evicts by has to match the key the query wrote; if CacheScope's " +
            "two halves ever drift, this is the only test that notices");
    }

    [Fact]
    public async Task SendAsync_WhenTheCommandFails_DoesNotEvict()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<FailingTouchHandler>(UserOf(Alice));
        var queries = provider.GetRequiredService<IQueryDispatcher>();
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        await queries.SendAsync(new Lookup(1), CancellationToken.None);
        await commands.SendAsync(new Touch(1), CancellationToken.None);
        await queries.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(
            1, "nothing changed, so there is nothing to invalidate");
    }

    [Fact]
    public async Task SendAsync_ForACommandWithoutIInvalidatesCache_DoesNotEvict()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<TouchHandler>(UserOf(Alice));
        var queries = provider.GetRequiredService<IQueryDispatcher>();
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        await queries.SendAsync(new Lookup(1), CancellationToken.None);
        await commands.SendAsync(new Quiet(1), CancellationToken.None);
        await queries.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(1, "eviction is opt-in, like caching");
    }

    [Fact]
    public async Task SendAsync_WhenCachingIsDisabled_DoesNotThrowOnAnInvalidatingCommand()
    {
        await using var provider = Build<TouchHandler>(UserOf(Alice), enabled: false);
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        var result = await commands.SendAsync(new Touch(1), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "the kill switch has to leave writes working, or the integration suites cannot run");
    }

    [Fact]
    public async Task SendAsync_ForAnInvalidatingCommandWithNoCurrentUser_Throws()
    {
        await using var provider = Build<TouchHandler>(UserOf(null));
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        var act = async () => await commands.SendAsync(new Touch(1), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Touch*")
            .WithMessage("*current user*");
    }

    [Fact]
    public async Task SendAsync_OneCallersWrite_DoesNotEvictAnotherCallersEntries()
    {
        CountingLookupHandler.Calls = 0;
        var currentUser = Substitute.For<ICurrentUser>();

        // A lazy callback keyed on a mutable local, not a positional Returns(Alice, Bob, Alice)
        // sequence: nothing guarantees ICurrentUser.Id is read exactly once per dispatch — an
        // audit behavior reading it inside ValidateAsync, say, would slide a positional sequence
        // by one and either fail this test for the wrong reason or, worse, pass it coincidentally
        // while no longer proving caller isolation. Fixing the id per dispatch instead makes the
        // stub correct no matter how many times production code reads it within one dispatch.
        Guid? currentUserId = Alice;
        currentUser.Id.Returns(_ => currentUserId);
        await using var provider = Build<TouchHandler>(currentUser);
        var queries = provider.GetRequiredService<IQueryDispatcher>();
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        // Alice reads, Bob writes, Alice reads again.
        await queries.SendAsync(new Lookup(1), CancellationToken.None);
        currentUserId = Bob;
        await commands.SendAsync(new Touch(1), CancellationToken.None);
        currentUserId = Alice;
        await queries.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(
            1, "Bob's write must not invalidate Alice's cached page");
    }
}

// CA1711: the name ends in "Collection" without implementing ICollection<T>. This is the xUnit
// collection-definition idiom - an empty marker type named after the group it serializes,
// referenced only via nameof() in [Collection(nameof(CacheBehaviorCollection))] - not a general
// collection type, so the rule's intent (avoid confusing a type for a collection API) does not
// apply here. Unlike ApiFactoryCollection/PostgresCollection this needs no ICollectionFixture<T>:
// there is nothing to share, only two classes to keep off xUnit's default per-class parallelism.
// QueryCachingBehaviorTests and CacheEvictionBehaviorTests both mutate the same static
// CountingLookupHandler.Calls counter (and its siblings' counters); running their collections
// concurrently would race on it, so both join this one collection to force them to run
// sequentially instead.
#pragma warning disable CA1711
[CollectionDefinition(nameof(CacheBehaviorCollection))]
public sealed class CacheBehaviorCollection;
#pragma warning restore CA1711
