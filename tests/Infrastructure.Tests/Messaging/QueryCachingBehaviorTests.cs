using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Caching;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.Messaging;

/// <summary>A cacheable query, and a handler that counts how often it actually ran.</summary>
public sealed record Lookup(int Value) : IQuery<string>, ICacheable
{
    public string CacheKey => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public TimeSpan Duration => TimeSpan.FromSeconds(30);
}

public sealed class CountingLookupHandler : IQueryHandler<Lookup, string>
{
    // Static because the handler is registered scoped: each dispatch resolves a new instance,
    // so an instance field could not tell one call from two. Internal, not public: CA2211/MA0069
    // flag a public mutable static field, and nothing outside this assembly needs to see it.
    // Safe to mutate from tests only because QueryCachingBehaviorTests and
    // CacheEvictionBehaviorTests both join CacheBehaviorCollection, which keeps xUnit from
    // running their collections concurrently — a class added here that touches this counter
    // must join that collection too.
    internal static int Calls;

    public Task<Result<string>> HandleAsync(Lookup query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Interlocked.Increment(ref Calls);
        return Task.FromResult(Result.Success($"value-{query.Value}"));
    }
}

public sealed class FailingLookupHandler : IQueryHandler<Lookup, string>
{
    // Internal, not public: CA2211/MA0069 flag a public mutable static field, and nothing
    // outside this assembly needs to see it.
    internal static int Calls;

    public Task<Result<string>> HandleAsync(Lookup query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Interlocked.Increment(ref Calls);
        return Task.FromResult(Result.Failure<string>(
            new Error(ErrorKind.NotFound, "lookup.not_found", "no such value")));
    }
}

/// <summary>An uncacheable query, to prove opt-in really is opt-in.</summary>
public sealed record Plain(int Value) : IQuery<string>;

public sealed class PlainHandler : IQueryHandler<Plain, string>
{
    // Internal, not public: CA2211/MA0069 flag a public mutable static field, and nothing
    // outside this assembly needs to see it.
    internal static int Calls;

    public Task<Result<string>> HandleAsync(Plain query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Interlocked.Increment(ref Calls);
        return Task.FromResult(Result.Success("plain"));
    }
}

[Collection(nameof(CacheBehaviorCollection))]
public sealed class QueryCachingBehaviorTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // A real HybridCache, not a substitute: the thing under test is whether the key and tag
    // composition actually hits and misses, which a mock would simply agree with.
    private static ServiceProvider Build<THandler>(ICurrentUser currentUser, bool enabled = true)
        where THandler : class, IQueryHandler<Lookup, string>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        services.Configure<CacheOptions>(o => o.Enabled = enabled);
        services.AddQuery<Lookup, string, THandler>();
        services.AddQuery<Plain, string, PlainHandler>();
        services.AddSingleton<QueryRegistry>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();
        services.AddSingleton(currentUser);

        return services.BuildServiceProvider();
    }

    private static ICurrentUser UserOf(Guid? id)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(id);
        return currentUser;
    }

    [Fact]
    public async Task SendAsync_TwiceForTheSameUserAndArguments_RunsTheHandlerOnce()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(Alice));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var first = await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);
        var second = await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);

        first.Value.Should().Be("value-1");
        second.Value.Should().Be("value-1");
        CountingLookupHandler.Calls.Should().Be(1, "the second dispatch must be served from cache");
    }

    [Fact]
    public async Task SendAsync_ForASecondUser_DoesNotServeTheFirstUsersValue()
    {
        CountingLookupHandler.Calls = 0;
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(Alice, Bob);
        await using var provider = Build<CountingLookupHandler>(currentUser);
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);
        await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(
            2,
            "the same CacheKey for a different caller is a different entry; sharing it would " +
            "serve one user another user's data");
    }

    [Fact]
    public async Task SendAsync_WithDifferentArguments_RunsTheHandlerForEach()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(Alice));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);
        await dispatcher.SendAsync(new Lookup(2), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task SendAsync_ForACacheableQueryWithNoCurrentUser_Throws()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(null));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var act = async () => await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Lookup*")
            .WithMessage("*current user*");
    }

    [Fact]
    public async Task SendAsync_WhenTheHandlerFails_ReturnsTheFailureAndCachesNothing()
    {
        FailingLookupHandler.Calls = 0;
        await using var provider = Build<FailingLookupHandler>(UserOf(Alice));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var first = await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);
        var second = await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);

        first.IsSuccess.Should().BeFalse();
        first.Error.Kind.Should().Be(ErrorKind.NotFound);
        first.Error.Code.Should().Be("lookup.not_found");
        second.IsSuccess.Should().BeFalse();
        FailingLookupHandler.Calls.Should().Be(
            2, "a 404 must not be retained for the query's full duration");
    }

    [Fact]
    public async Task SendAsync_WhenCachingIsDisabled_RunsTheHandlerEveryTime()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(Alice), enabled: false);
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);
        await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(
            2, "the integration and e2e suites depend on this switch actually switching");
    }

    [Fact]
    public async Task SendAsync_ForAQueryWithoutICacheable_RunsTheHandlerEveryTime()
    {
        PlainHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(Alice));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        await dispatcher.SendAsync(new Plain(1), CancellationToken.None);
        await dispatcher.SendAsync(new Plain(1), CancellationToken.None);

        PlainHandler.Calls.Should().Be(2, "caching is opt-in, not the default");
    }

    [Fact]
    public async Task SendAsync_ForAQueryWithoutICacheableAndNoCurrentUser_DoesNotThrow()
    {
        PlainHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(null));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var result = await dispatcher.SendAsync(new Plain(1), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "the current-user requirement belongs to caching, not to every query — the outbox " +
            "pumps resolve scopes with no HTTP context at all");
    }
}
