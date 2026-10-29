using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests;

/// <summary>
/// The error statuses the committed contract declares, which is what frontend/src/api/schema.d.ts
/// types a client against. Read from the shared ApiFactory host, which runs in Development and so
/// serves the document, rather than from a host of its own: each extra host costs a full start.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class OpenApiResponsesTests(ApiFactory factory)
{
    [Theory]
    [InlineData("/api/orders", "get")]
    [InlineData("/api/orders/exports", "post")]
    [InlineData("/api/notifications/unread-count", "get")]
    public async Task ASignedInEndpoint_Declares401(string path, string method)
    {
        var responses = await ResponsesOf(path, method);

        responses.Should().Contain("401", "the cookie handler answers 401 for any request without a session");
    }

    [Theory]
    [InlineData("/api/fulfilment/orders", "get")]
    [InlineData("/api/monitoring/access", "get")]
    public async Task ACapabilityEndpoint_Declares403(string path, string method)
    {
        var responses = await ResponsesOf(path, method);

        responses.Should().Contain("403", "a member without the capability is refused with 403");
    }

    [Fact]
    public async Task ASignedInEndpointWithoutACapability_DeclaresNo403()
    {
        var responses = await ResponsesOf("/api/orders", "get");

        responses.Should().NotContain("403");
    }

    [Theory]
    [InlineData("/api/auth/register", "post")]
    [InlineData("/api/auth/logout", "post")]
    public async Task AnAnonymousActionUnderAnAuthorizedController_DeclaresNo401(string path, string method)
    {
        var responses = await ResponsesOf(path, method);

        responses.Should().NotContain("401", "[AllowAnonymous] on the action overrides the controller's [Authorize]");
    }

    [Fact]
    public async Task ASignedInEndpointWithNoErrorsOfItsOwn_StillDeclaresItsSuccess()
    {
        var responses = await ResponsesOf("/api/orders/exports", "get");

        responses.Should().Contain("200",
            "declaring an error must not drop the success ApiExplorer would otherwise infer");
    }

    [Fact]
    public async Task Rates_Declares404()
    {
        var responses = await ResponsesOf("/api/rates", "get");

        responses.Should().Contain("404", "the provider answers rates.pair_not_found for a pair it does not quote");
    }

    private async Task<IReadOnlyList<string>> ResponsesOf(string path, string method)
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        return [.. document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("responses").EnumerateObject().Select(response => response.Name)];
    }
}
