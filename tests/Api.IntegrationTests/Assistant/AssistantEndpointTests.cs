extern alias simulator;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using AiFramework.Domain.Users;
using simulator::AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Api.IntegrationTests.Assistant;

[Collection(nameof(ApiFactoryCollection))]
public sealed class AssistantEndpointTests(ApiFactory factory) : IAsyncLifetime
{
    private readonly string _keyFile = Path.GetTempFileName();
    private FakeMessagesApi _fake = null!;
    private WebApplicationFactory<Program> _enabled = null!;

    public async Task InitializeAsync()
    {
        await File.WriteAllTextAsync(_keyFile, "sk-test");
        _fake = await FakeMessagesApi.StartAsync();
        _enabled = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Assistant:Enabled", "true");
            b.UseSetting("Assistant:ApiKeyFile", _keyFile);
            b.UseSetting("Assistant:BaseUrl", _fake.BaseAddress.ToString().TrimEnd('/'));
            b.UseSetting("Assistant:MaxRetries", "0");
            b.UseSetting("RateLimiting:Assistant:PermitLimit", "1000");
        });
    }

    public async Task DisposeAsync()
    {
        await _enabled.DisposeAsync();
        await _fake.DisposeAsync();
        File.Delete(_keyFile);
    }

    private Task<HttpClient> AdminAsync() => AdminAsync(_enabled);

    private async Task<HttpClient> AdminAsync(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        var username = $"a{Guid.NewGuid():N}"[..32];
        (await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Username = username, Password = ApiFactory.RegisteredPassword, DisplayName = "Admin" })).EnsureSuccessStatusCode();
        await factory.SetRoleAsync(username, UserRole.Admin);
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
    public async Task Status_WhenDisabled_ReportsDisabled()
    {
        var client = await factory.CreateAdminClientAsync();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/assistant");

        status.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Ask_WhenDisabled_IsNotFound()
    {
        var client = await factory.CreateAdminClientAsync();

        var response = await client.PostAsJsonAsync("/api/assistant/messages", new { question = "Why?" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ask_Anonymous_IsUnauthorized()
    {
        var response = await _enabled.CreateClient().PostAsJsonAsync("/api/assistant/messages", new { question = "Why?" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ask_NonAdmin_IsForbidden()
    {
        var client = _enabled.CreateClient();
        (await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Username = $"u{Guid.NewGuid():N}"[..32], Password = ApiFactory.RegisteredPassword, DisplayName = "U" })).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/assistant/messages", new { question = "Why?" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Ask_BlankQuestion_IsAProblemBeforeAnyStream()
    {
        var client = await AdminAsync();

        var response = await client.PostAsJsonAsync("/api/assistant/messages", new { question = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Ask_StreamsToolTextAndDoneInOrderWithoutBuffering()
    {
        _fake.Enqueue(FakeReply.ToolUse("toolu_1", "job_health", """{"window_hours":24}"""));
        _fake.Enqueue(FakeReply.Text("Nothing ", "failed."));
        var client = await AdminAsync();

        var response = await client.PostAsJsonAsync("/api/assistant/messages", new { question = "Did jobs fail?" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.GetValues("X-Accel-Buffering").Should().Equal("no");
        var events = await EventsAsync(response);
        events.Select(e => e.GetProperty("type").GetString()).Should().Equal("tool", "text", "text", "done");
        events[0].GetProperty("tool").GetString().Should().Be("job_health");
        events[^1].GetProperty("outcome").GetString().Should().Be("Answered");
    }

    [Fact]
    public async Task Ask_FollowUp_ContinuesTheConversation()
    {
        _fake.Enqueue(FakeReply.Text("First."));
        _fake.Enqueue(FakeReply.Text("Second."));
        var client = await AdminAsync();
        var first = await EventsAsync(await client.PostAsJsonAsync("/api/assistant/messages", new { question = "One?" }));
        var conversationId = first[^1].GetProperty("conversationId").GetGuid();

        var second = await EventsAsync(await client.PostAsJsonAsync("/api/assistant/messages", new { conversationId, question = "Two?" }));

        second[^1].GetProperty("conversationId").GetGuid().Should().Be(conversationId);
        _fake.Requests[^1].Body.GetProperty("messages").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Ask_AnotherAdminsConversation_IsNotFound()
    {
        _fake.Enqueue(FakeReply.Text("Mine."));
        var owner = await AdminAsync();
        var first = await EventsAsync(await owner.PostAsJsonAsync("/api/assistant/messages", new { question = "One?" }));
        var other = await AdminAsync();

        var response = await other.PostAsJsonAsync(
            "/api/assistant/messages",
            new { conversationId = first[^1].GetProperty("conversationId").GetGuid(), question = "Peek?" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ask_ProviderFailsAfterTheStreamStarted_EndsWithAnErrorEvent()
    {
        _fake.Enqueue(FakeReply.ToolUse("toolu_1", "job_health", """{"window_hours":24}"""));
        _fake.Enqueue(FakeReply.Status(500));
        var client = await AdminAsync();

        var events = await EventsAsync(await client.PostAsJsonAsync("/api/assistant/messages", new { question = "Why?" }));

        events[^1].GetProperty("type").GetString().Should().Be("error");
        events[^1].GetProperty("problem").GetProperty("title").GetString().Should().Be("assistant.unavailable");
    }

    // Once the stream has started, an unanticipated exception (a tool query's database fault, say)
    // can no longer become a status code. It must end the stream with an error event rather than
    // reset the connection, and leak nothing of the exception (final review, Important 3).
    [Fact]
    public async Task Ask_UnexpectedExceptionAfterTheStreamStarted_EndsWithAnErrorEventAndLeaksNothing()
    {
        await using var throwing = _enabled.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddScoped<IAssistantModel, TextThenThrowModel>()));
        var client = await AdminAsync(throwing);

        var response = await client.PostAsJsonAsync("/api/assistant/messages", new { question = "Why?" });
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("SECRET-CONNECTION-STRING");
        var events = await EventsAsync(response);
        events.Select(e => e.GetProperty("type").GetString()).Should().Equal("text", "error");
        events[^1].GetProperty("problem").GetProperty("title").GetString().Should().Be("internal_error");
    }

    [Fact]
    public async Task Ask_ProviderFailsBeforeAnyEvent_IsAServiceUnavailableProblem()
    {
        _fake.Enqueue(FakeReply.Status(503));
        var client = await AdminAsync();

        var response = await client.PostAsJsonAsync("/api/assistant/messages", new { question = "Why?" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
    }

    private sealed class TextThenThrowModel : IAssistantModel
    {
        public async Task<Result<AssistantModelTurn>> NextTurnAsync(
            AssistantRequest request, IAssistantOutput output, CancellationToken cancellationToken)
        {
            await output.TextAsync("partial answer", cancellationToken);
            throw new InvalidOperationException("SECRET-CONNECTION-STRING");
        }
    }
}
