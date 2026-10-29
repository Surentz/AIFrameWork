using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Diagnostics;

/// <summary>
/// The error responses no controller writes: the cookie handler's 401 and 403, and routing's 404
/// and 405. Each used to go out with an empty body and no trace id, unlike every error a controller
/// or GlobalExceptionHandler produces, so a client showed "Request failed with status 403." and an
/// operator had nothing to search the logs for.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class StatusCodeProblemTests(ApiFactory factory)
{
    [Fact]
    public async Task ARequestWithNoSession_GetsA401Problem()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/orders");

        await ShouldBeAProblem(response, HttpStatusCode.Unauthorized, "auth.unauthenticated");
    }

    [Fact]
    public async Task AMemberOnAnAdministratorEndpoint_GetsA403Problem()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/monitoring/users");

        await ShouldBeAProblem(response, HttpStatusCode.Forbidden, "auth.forbidden");
    }

    [Fact]
    public async Task AnAddressNoEndpointMatches_GetsA404Problem()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/no-such-thing");

        await ShouldBeAProblem(response, HttpStatusCode.NotFound, "route.not_found");
    }

    [Fact]
    public async Task AMethodTheAddressDoesNotAccept_GetsA405Problem()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.DeleteAsync("/api/orders");

        await ShouldBeAProblem(response, HttpStatusCode.MethodNotAllowed, "route.method_not_allowed");
    }

    [Fact]
    public async Task AControllersOwnProblem_IsLeftAsTheControllerWroteIt()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/orders/{Guid.NewGuid()}");

        await ShouldBeAProblem(response, HttpStatusCode.NotFound, "order.not_found");
    }

    [Fact]
    public async Task ASuccessWithNoBody_StaysEmpty()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/auth/logout", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    private static async Task ShouldBeAProblem(
        HttpResponseMessage response, HttpStatusCode status, string title)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType.Should().NotBeNull("an empty body carries no content type");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("status").GetInt32().Should().Be((int)status);
        root.GetProperty("title").GetString().Should().Be(title);
        root.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace(
            "the trace id is how an operator holding the error finds its log lines");
    }
}
