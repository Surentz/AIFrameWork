using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Domain.Users;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Assistant;

[Collection(nameof(ApiFactoryCollection))]
public sealed class AssistantQuickEndpointTests(ApiFactory factory) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _quick = null!;

    public Task InitializeAsync()
    {
        // Instant answers on, the AI off: nothing here may reach a model.
        _quick = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Assistant:Quick:Enabled", "true");
            b.UseSetting("RateLimiting:AssistantQuick:PermitLimit", "1000");
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _quick.DisposeAsync();

    private async Task<HttpClient> MemberAsync(WebApplicationFactory<Program>? host = null)
    {
        var client = (host ?? _quick).CreateClient();
        (await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Username = $"m{Guid.NewGuid():N}"[..32], Password = ApiFactory.RegisteredPassword, DisplayName = "Member" })).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<List<JsonElement>> EventsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return [.. body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..])
            .Select(json => JsonDocument.Parse(json).RootElement.Clone())];
    }

    [Fact]
    public async Task Status_ReportsEachHalf()
    {
        var client = await MemberAsync();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/assistant");

        status.GetProperty("quick").GetBoolean().Should().BeTrue();
        status.GetProperty("ai").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Status_WhenTheIndexDidNotBuild_ReportsQuickOff()
    {
        await using var unbuilt = WithoutAModel();
        var client = await MemberAsync(unbuilt);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/assistant");

        status.GetProperty("quick").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Quick_WhenTheIndexDidNotBuild_IsUnavailable_AndSavesNothing()
    {
        await using var unbuilt = WithoutAModel();
        var client = await MemberAsync(unbuilt);

        var response = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Has my last order shipped?" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("assistant.quick_unavailable");
        var saved = await client.GetFromJsonAsync<JsonElement>("/api/assistant/conversations");
        saved.GetArrayLength().Should().Be(0);
    }

    // Instant answers switched on, but the model folder is empty, so the start-up build publishes nothing.
    private WebApplicationFactory<Program> WithoutAModel() => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Assistant:Quick:Enabled", "true");
        b.UseSetting("Assistant:Quick:ModelPath", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        b.UseSetting("RateLimiting:AssistantQuick:PermitLimit", "1000");
    });

    [Fact]
    public async Task Quick_PutsTheAnswersTurnOnItsEventsAndOnDone()
    {
        var client = await MemberAsync();

        var events = await EventsAsync(await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Has my last order shipped?" }));

        events.Should().HaveCount(2);
        events[0].GetProperty("turn").GetInt32().Should().Be(1);
        events[1].GetProperty("type").GetString().Should().Be("done");
        events[1].GetProperty("turn").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Quick_AnswersAMembersOrderQuestionInstantly_AndSavesIt()
    {
        var client = await MemberAsync();

        var response = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Has my last order shipped?" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await EventsAsync(response);
        events.Select(e => e.GetProperty("type").GetString()).Should().Equal("answer", "done");
        var answer = events[0].GetProperty("answer");
        answer.GetProperty("kind").GetString().Should().Be("Instant");
        answer.GetProperty("text").GetString().Should().Be("You haven't placed any orders yet.");
        answer.GetProperty("link").GetProperty("to").GetString().Should().Be("/orders/new");

        var id = events[1].GetProperty("conversationId").GetString();
        var saved = await client.GetFromJsonAsync<JsonElement>($"/api/assistant/conversations/{id}");
        saved.GetProperty("messages")[1].GetProperty("answer").GetProperty("text").GetString()
            .Should().Be("You haven't placed any orders yet.");
    }

    [Fact]
    public async Task Quick_OffersSuggestions_ForAQuestionItCannotPlace_WithoutTheAi()
    {
        var client = await MemberAsync();

        var response = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Write me a poem about the rain" });

        var events = await EventsAsync(response);
        events[0].GetProperty("type").GetString().Should().Be("suggestions");
        events[0].GetProperty("suggestions").GetProperty("aiAvailable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Quick_NeverGivesAMemberMonitoringData()
    {
        var client = await MemberAsync();

        var response = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Did any background job fail today?" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("/monitoring");
        body.Should().NotContain("background job failed");
        var reply = (await EventsAsync(response))[0];
        reply.GetProperty("type").GetString().Should().BeOneOf("answer", "suggestions");
        var options = reply.TryGetProperty("suggestions", out var suggestions)
            ? suggestions.GetProperty("options").EnumerateArray().Select(o => o.GetString()).ToList()
            : [];
        options.Should().NotIntersectWith(QuickIntents.All.Where(i => i.Scope == ToolScope.Operator).Select(i => i.CanonicalQuestion));
    }

    [Fact]
    public async Task Quick_GivesAnOperatorJobHealth()
    {
        var client = _quick.CreateClient();
        var username = $"a{Guid.NewGuid():N}"[..32];
        (await client.PostAsJsonAsync("/api/auth/register", new { Username = username, Password = ApiFactory.RegisteredPassword, DisplayName = "Admin" })).EnsureSuccessStatusCode();
        await factory.SetRoleAsync(username, UserRole.Admin);

        var response = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Did any background job fail today?" });

        var events = await EventsAsync(response);
        events[0].GetProperty("answer").GetProperty("link").GetProperty("to").GetString().Should().Be("/monitoring/jobs");
    }

    [Fact]
    public async Task Quick_WhenSwitchedOff_IsNotFound()
    {
        var client = await MemberAsync(factory);

        var response = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Has my last order shipped?" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Quick_HasItsOwnRateLimit()
    {
        await using var limited = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Assistant:Quick:Enabled", "true");
            b.UseSetting("RateLimiting:AssistantQuick:PermitLimit", "1");
        });
        var client = await MemberAsync(limited);

        (await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Show my recent orders" })).EnsureSuccessStatusCode();
        var second = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Show my recent orders" });

        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
