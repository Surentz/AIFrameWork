using System.Net;
using System.Net.Http.Json;
using System.Text;
using AiFramework.Api.IntegrationTests.Messaging;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Jobs;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Api.IntegrationTests.Orders;

/// <summary>
/// The export endpoints over real HTTP. The build job runs in the worker and the API listens to no
/// job queue, so <see cref="CompleteAsync"/> stands in for it: it completes the export through the
/// aggregate exactly as <c>CompleteOrderExport</c> does, which writes the same outbox row.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class OrderExportsEndpointTests(ApiFactory factory)
{
    private static readonly byte[] PdfBytes = "%PDF-1.7 test"u8.ToArray();

    private sealed record ExportItem(
        Guid Id, string Status, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt, int? RowCount);

    private sealed record NotificationItem(Guid Id, string Kind, string Title, string Body, Guid? SubjectId);

    private sealed record NotificationPage(IReadOnlyList<NotificationItem> Items, string? NextCursor);

    private static async Task<ExportItem> RequestAsync(HttpClient client)
    {
        var response = await client.PostAsync(new Uri("/api/orders/exports", UriKind.Relative), null);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<ExportItem>())!;
    }

    private async Task CompleteAsync(Guid exportId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var export = await context.OrderExports.SingleAsync(e => e.Id == exportId);
        export.Complete(PdfBytes, 1, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Request_WithoutASession_IsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync(new Uri("/api/orders/exports", UriKind.Relative), null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_AcceptsAndReportsRequested()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync(new Uri("/api/orders/exports", UriKind.Relative), null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().Be(new Uri("/api/orders/exports", UriKind.Relative));
        var export = await response.Content.ReadFromJsonAsync<ExportItem>();
        export!.Status.Should().Be("Requested");
    }

    [Fact]
    public async Task Request_WhileOneIsInProgress_ReturnsThatOne()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var first = await RequestAsync(client);

        var second = await RequestAsync(client);

        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task List_ShowsTheCallersExportsAndNoOneElses()
    {
        using var mine = await factory.CreateAuthenticatedClientAsync();
        using var theirs = await factory.CreateAuthenticatedClientAsync();
        var export = await RequestAsync(mine);
        await RequestAsync(theirs);

        var list = await mine.GetFromJsonAsync<List<ExportItem>>(new Uri("/api/orders/exports", UriKind.Relative));

        list.Should().ContainSingle().Which.Id.Should().Be(export.Id);
    }

    [Fact]
    public async Task List_IsNeverStored()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync(new Uri("/api/orders/exports", UriKind.Relative));

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Download_BeforeTheFileIsBuilt_IsNotFound()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var export = await RequestAsync(client);

        var response = await client.GetAsync(new Uri($"/api/orders/exports/{export.Id}/download", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_OfSomeoneElsesExport_IsNotFound()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync();
        using var stranger = await factory.CreateAuthenticatedClientAsync();
        var export = await RequestAsync(owner);
        await CompleteAsync(export.Id);

        var response = await stranger.GetAsync(new Uri($"/api/orders/exports/{export.Id}/download", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_OfABuiltExport_IsThePdfAsAnAttachment()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var export = await RequestAsync(client);
        await CompleteAsync(export.Id);

        var response = await client.GetAsync(new Uri($"/api/orders/exports/{export.Id}/download", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName.Should()
            .Be($"orders-{export.RequestedAt.UtcDateTime:yyyy-MM-dd}.pdf");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(PdfBytes);
    }

    [Fact]
    public async Task List_AfterCompletion_ShowsReadyWithItsRowCount()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var export = await RequestAsync(client);
        await CompleteAsync(export.Id);

        var list = await client.GetFromJsonAsync<List<ExportItem>>(new Uri("/api/orders/exports", UriKind.Relative));

        list.Should().ContainSingle().Which.Should().Match<ExportItem>(e => e.Status == "Ready" && e.RowCount == 1);
    }

    /// <summary>
    /// The request's only route to a build: OrderExportRequested, delivered off the outbox, enqueues
    /// BuildOrderExport. Without this, dropping that event's registration or its handler would leave
    /// every other test here green while no export were ever built. The API listens to no job queue,
    /// so the message waits on the heavy lane's queue, where the probe reads it.
    /// </summary>
    [Fact]
    public async Task Request_ThenDraining_EnqueuesTheBuildOnTheHeavyLane()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var export = await RequestAsync(client);

        await factory.DrainOutboxUntilEmptyAsync();

        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var queue = JobRegistration.QueueFor(JobLane.Heavy);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        string? body = null;
        while (body is null && DateTime.UtcNow < deadline)
        {
            // Other tests here request exports too, and nothing consumes this queue in the API host,
            // so read until this export's message turns up rather than taking the first one.
            var message = await probe.WaitForMessageAsync(queue, TimeSpan.FromSeconds(5));
            var text = message is null ? null : Encoding.UTF8.GetString(message.Body.Span);
            if (text is not null && text.Contains(export.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                body = text;
            }
        }

        body.Should().NotBeNull($"a BuildOrderExport for {export.Id} should be waiting on {queue}");
    }

    [Fact]
    public async Task Completion_ThenDraining_TellsTheOwnerItIsReady()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var export = await RequestAsync(client);
        await CompleteAsync(export.Id);

        await factory.DrainOutboxUntilEmptyAsync();

        var page = await client.GetFromJsonAsync<NotificationPage>(new Uri("/api/notifications", UriKind.Relative));
        page!.Items.Should().ContainSingle(n => n.SubjectId == export.Id)
            .Which.Kind.Should().Be("OrderExportReady");
    }
}
