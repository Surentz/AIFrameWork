using System.Net;
using System.Net.Http.Json;
using AiFramework.Api.IntegrationTests.Orders;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Notifications;

/// <summary>
/// The feed over real HTTP, end to end: place an order, drain the outbox, read what the notifier
/// wrote. Driving delivery by hand rather than waiting on the hosted pumps, per tests/CLAUDE.md.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class NotificationsEndpointTests(ApiFactory factory)
{
    private sealed record NotificationItem(
        Guid Id, string Kind, string Title, string Body, Guid? SubjectId,
        DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

    private sealed record NotificationPage(IReadOnlyList<NotificationItem> Items, string? NextCursor);

    private sealed record UnreadCountResult(int UnreadCount);

    private sealed record ReadResult(int MarkedCount, int UnreadCount);

    private static async Task<Guid> PlaceOrderAsync(HttpClient client)
    {
        var sku = await CatalogueSetup.CreateProductAsync(client);
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = sku, Quantity = 2 });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    private static async Task<NotificationPage> ListAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/notifications{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<NotificationPage>())!;
    }

    [Fact]
    public async Task GetNotifications_WithoutASession_IsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/notifications");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PlacingAnOrderAndDraining_WritesANotificationForTheBuyer()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(client);

        await factory.DrainOutboxUntilEmptyAsync();

        var page = await ListAsync(client);
        page.Items.Should().ContainSingle(n => n.SubjectId == orderId)
            .Which.Kind.Should().Be("OrderPlaced");
    }

    [Fact]
    public async Task NotificationKind_IsSerializedAsAName()
    {
        // Not as a number. The whole reason Program.cs registers a JsonStringEnumConverter - a
        // client switching on this gets a string union in schema.d.ts rather than an int.
        using var client = await factory.CreateAuthenticatedClientAsync();
        await PlaceOrderAsync(client);
        await factory.DrainOutboxUntilEmptyAsync();

        var body = await client.GetStringAsync("/api/notifications");

        body.Should().Contain("\"kind\":\"OrderPlaced\"");
    }

    [Fact]
    public async Task DrainingTwice_DoesNotDuplicateTheNotification()
    {
        // Delivery is at-least-once. This is the assertion the unique index and the dedupe check
        // exist for.
        using var client = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(client);

        await factory.DrainOutboxUntilEmptyAsync();
        await factory.DrainOutboxUntilEmptyAsync();

        var page = await ListAsync(client);
        page.Items.Where(n => n.SubjectId == orderId).Should().HaveCount(1);
    }

    [Fact]
    public async Task GetUnreadCount_CountsWhatTheFeedShowsUnread()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        await PlaceOrderAsync(client);
        await factory.DrainOutboxUntilEmptyAsync();

        var response = await client.GetAsync("/api/notifications/unread-count");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var count = await response.Content.ReadFromJsonAsync<UnreadCountResult>();

        var page = await ListAsync(client, "?unreadOnly=true");
        count!.UnreadCount.Should().Be(page.Items.Count);
    }

    [Fact]
    public async Task MarkRead_MarksItAndDropsTheUnreadCount()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        await PlaceOrderAsync(client);
        await factory.DrainOutboxUntilEmptyAsync();

        var before = (await client.GetFromJsonAsync<UnreadCountResult>(
            "/api/notifications/unread-count"))!.UnreadCount;
        var target = (await ListAsync(client, "?unreadOnly=true")).Items[0];

        var response = await client.PostAsync($"/api/notifications/{target.Id}/read", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReadResult>();

        result!.MarkedCount.Should().Be(1);
        result.UnreadCount.Should().Be(before - 1);
    }

    [Fact]
    public async Task MarkRead_ReportsACountTheNextReadAgreesWith()
    {
        // The handler subtracts its own change from a count taken before commit. This proves the
        // arithmetic matches what the database actually says afterwards.
        using var client = await factory.CreateAuthenticatedClientAsync();
        await PlaceOrderAsync(client);
        await factory.DrainOutboxUntilEmptyAsync();
        var target = (await ListAsync(client, "?unreadOnly=true")).Items[0];

        var response = await client.PostAsync($"/api/notifications/{target.Id}/read", null);
        var reported = (await response.Content.ReadFromJsonAsync<ReadResult>())!.UnreadCount;

        var actual = (await client.GetFromJsonAsync<UnreadCountResult>(
            "/api/notifications/unread-count"))!.UnreadCount;
        reported.Should().Be(actual);
    }

    [Fact]
    public async Task MarkRead_Twice_IsIdempotent()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        await PlaceOrderAsync(client);
        await factory.DrainOutboxUntilEmptyAsync();
        var target = (await ListAsync(client, "?unreadOnly=true")).Items[0];

        await client.PostAsync($"/api/notifications/{target.Id}/read", null);
        var second = await client.PostAsync($"/api/notifications/{target.Id}/read", null);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await second.Content.ReadFromJsonAsync<ReadResult>();
        result!.MarkedCount.Should().Be(0);
    }

    [Fact]
    public async Task MarkRead_ForAnotherUsersNotification_IsNotFound()
    {
        // Not 403: confirming the id is real to someone who does not own it is itself a leak.
        using var owner = await factory.CreateAuthenticatedClientAsync();
        await PlaceOrderAsync(owner);
        await factory.DrainOutboxUntilEmptyAsync();
        var target = (await ListAsync(owner, "?unreadOnly=true")).Items[0];

        using var stranger = await factory.CreateAuthenticatedClientAsync();
        var response = await stranger.PostAsync($"/api/notifications/{target.Id}/read", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetNotifications_ShowsOnlyTheCallersOwn()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(owner);
        await factory.DrainOutboxUntilEmptyAsync();

        using var stranger = await factory.CreateAuthenticatedClientAsync();
        var page = await ListAsync(stranger);

        page.Items.Should().NotContain(n => n.SubjectId == orderId);
    }

    [Fact]
    public async Task MarkAllRead_LeavesNothingUnread()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        await PlaceOrderAsync(client);
        await PlaceOrderAsync(client);
        await factory.DrainOutboxUntilEmptyAsync();

        var response = await client.PostAsync("/api/notifications/read-all", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReadResult>();

        result!.UnreadCount.Should().Be(0);
        var after = (await client.GetFromJsonAsync<UnreadCountResult>(
            "/api/notifications/unread-count"))!.UnreadCount;
        after.Should().Be(0);
    }

    [Fact]
    public async Task GetNotifications_WithAnOutOfRangeLimit_IsBadRequest()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/notifications?limit=0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetNotifications_WithAMalformedCursor_IsBadRequest()
    {
        // Never an empty first page - see KeysetCursor's remarks.
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/notifications?cursor=not-base64!!");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetNotifications_PagesWithoutRepeatingAnItem()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        await PlaceOrderAsync(client);
        await PlaceOrderAsync(client);
        await factory.DrainOutboxUntilEmptyAsync();

        var first = await ListAsync(client, "?limit=1");
        first.Items.Should().HaveCount(1);
        first.NextCursor.Should().NotBeNull();

        var second = await ListAsync(client, $"?limit=1&cursor={Uri.EscapeDataString(first.NextCursor)}");

        second.Items.Should().HaveCount(1);
        second.Items[0].Id.Should().NotBe(first.Items[0].Id);
    }
}
