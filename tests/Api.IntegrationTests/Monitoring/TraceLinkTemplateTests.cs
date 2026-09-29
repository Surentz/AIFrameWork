using System.Net.Http.Json;
using System.Text.Json;
using AiFramework.Api.Monitoring;
using AiFramework.Domain.Users;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace AiFramework.Api.IntegrationTests.Monitoring;

/// <summary>
/// The deep link ADR 0021 promised from a monitoring row to the log store. The template is
/// configuration, carried to the SPA on the admin-only access response, and refused at startup
/// when it could never produce a safe link — a template reaching the browser is rendered as an
/// <c>href</c>, so "validate later" would mean "validate in every browser".
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class TraceLinkTemplateTests(ApiFactory factory)
{
    private const string SeqTemplate =
        "http://localhost:55341/#/events?filter=@TraceId%20%3D%20'{traceId}'";

    private static readonly Uri Access = new("/api/monitoring/access", UriKind.Relative);

    private readonly ApiFactory _factory = factory;

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(SeqTemplate, true)]
    [InlineData("https://logs.example.com/trace/{traceId}", true)]
    [InlineData("https://logs.example.com/search", false)]
    [InlineData("/relative/{traceId}", false)]
    [InlineData("javascript:alert(1)//{traceId}", false)]
    [InlineData("ftp://logs.example.com/{traceId}", false)]
    public void IsValidTemplate_AcceptsOnlyAbsoluteHttpUrlsCarryingThePlaceholder(
        string? template, bool expected)
    {
        var valid = MonitoringPageOptions.IsValidTemplate(template);

        valid.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LinkTemplate_ForABlankSetting_IsNull(string? configured)
    {
        // An unset secret or an empty environment variable arrives as "" or whitespace; the SPA
        // must see "no template", not a template it would try to fill.
        var options = new MonitoringPageOptions { TraceLinkTemplate = configured };

        options.LinkTemplate.Should().BeNull();
    }

    [Fact]
    public async Task Access_WithNoTemplateConfigured_CarriesNull()
    {
        var client = await _factory.CreateAdminClientAsync();

        var template = await ReadTemplateAsync(client);

        template.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Access_WithATemplateConfigured_CarriesIt()
    {
        using var configured = _factory.WithWebHostBuilder(
            builder => builder.UseSetting("Monitoring:TraceLinkTemplate", SeqTemplate));
        var client = await AdminClientAsync(configured);

        var template = await ReadTemplateAsync(client);

        template.GetString().Should().Be(SeqTemplate);
    }

    [Fact]
    public void Startup_WithAnUnsafeTemplate_Fails()
    {
        using var misconfigured = _factory.WithWebHostBuilder(
            builder => builder.UseSetting("Monitoring:TraceLinkTemplate", "javascript:alert(1)//{traceId}"));

        var start = () => misconfigured.CreateClient();

        start.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Monitoring:TraceLinkTemplate");
    }

    /// <summary>
    /// An administrator session on a derived host. The derived host shares ApiFactory's database,
    /// so the role is set through the factory exactly as CreateAdminClientAsync does.
    /// </summary>
    private async Task<HttpClient> AdminClientAsync(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        var username = $"u{Guid.NewGuid():N}"[..32];

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Username = username, Password = ApiFactory.RegisteredPassword, DisplayName = "Test User" });
        response.EnsureSuccessStatusCode();

        await _factory.SetRoleAsync(username, UserRole.Admin);
        return client;
    }

    private static async Task<JsonElement> ReadTemplateAsync(HttpClient client)
    {
        var response = await client.GetAsync(Access);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("traceLinkTemplate").Clone();
    }
}
