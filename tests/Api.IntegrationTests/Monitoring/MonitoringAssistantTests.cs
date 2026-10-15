using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Monitoring;

[Collection(nameof(ApiFactoryCollection))]
public sealed class MonitoringAssistantTests(ApiFactory factory)
{
    [Fact]
    public async Task Insights_ForAnAdministrator_IsOk()
    {
        var admin = await factory.CreateAdminClientAsync();

        var insights = await admin.GetFromJsonAsync<JsonElement>("/api/monitoring/assistant/insights?days=7");

        insights.GetProperty("days").ValueKind.Should().Be(JsonValueKind.Array);
        insights.GetProperty("misses").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Insights_ForAMember_IsForbidden()
    {
        var member = await factory.CreateAuthenticatedClientAsync();

        (await member.GetAsync("/api/monitoring/assistant/insights?days=7")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Insights_AWindowOver30Days_IsBadRequest()
    {
        var admin = await factory.CreateAdminClientAsync();

        (await admin.GetAsync("/api/monitoring/assistant/insights?days=31")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
