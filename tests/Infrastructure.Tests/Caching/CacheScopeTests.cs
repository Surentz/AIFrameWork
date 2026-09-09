using AiFramework.Infrastructure.Messaging;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Caching;

/// <summary>
/// Key and Tag must agree, permanently. The read side writes an entry under Key and tags it with
/// Tag; the command side evicts by Tag. If the two ever stop sharing a prefix, eviction misses
/// every entry, silently, and every other test in the suite stays green.
/// </summary>
public sealed class CacheScopeTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Key_StartsWithTheTagForTheSameQueryAndUser()
    {
        var key = CacheScope.Key("GetOrders", Alice, "20:");
        var tag = CacheScope.Tag("GetOrders", Alice);

        key.Should().StartWith(
            tag, "RemoveByTagAsync can only reach an entry whose tag prefixes its key");
    }

    [Fact]
    public void Key_ForTwoDifferentUsers_Differs()
    {
        var alice = CacheScope.Key("GetOrders", Alice, "20:");
        var bob = CacheScope.Key("GetOrders", Bob, "20:");

        alice.Should().NotBe(
            bob, "a shared key across users is a data leak, not a staleness bug");
    }

    [Fact]
    public void Key_ForTwoDifferentQueryTypes_Differs()
    {
        var orders = CacheScope.Key("GetOrders", Alice, "1");
        var order = CacheScope.Key("GetOrder", Alice, "1");

        orders.Should().NotBe(order);
    }

    [Fact]
    public void Key_ForTwoDifferentArguments_Differs()
    {
        var first = CacheScope.Key("GetOrders", Alice, "20:");
        var second = CacheScope.Key("GetOrders", Alice, "50:");

        first.Should().NotBe(second);
    }

    [Fact]
    public void Tag_ForTwoDifferentUsers_Differs()
    {
        var alice = CacheScope.Tag("GetOrders", Alice);
        var bob = CacheScope.Tag("GetOrders", Bob);

        alice.Should().NotBe(
            bob, "evicting one caller's entries must not evict another's");
    }
}
