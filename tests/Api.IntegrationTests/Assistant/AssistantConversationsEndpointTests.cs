extern alias simulator;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using simulator::AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Assistant;

[Collection(nameof(ApiFactoryCollection))]
public sealed class AssistantConversationsEndpointTests(ApiFactory factory) : IAsyncLifetime
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

    private async Task<HttpClient> MemberAsync()
    {
        var client = _enabled.CreateClient();
        (await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Username = $"m{Guid.NewGuid():N}"[..32], Password = ApiFactory.RegisteredPassword, DisplayName = "Member" })).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<Guid> AskAsync(HttpClient client, string question, string answer)
    {
        _fake.Enqueue(FakeReply.Text(answer));
        var body = await (await client.PostAsJsonAsync("/api/assistant/messages", new { question })).Content.ReadAsStringAsync();
        var done = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n').Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..])
            .Select(json => JsonDocument.Parse(json).RootElement)
            .Last();
        return done.GetProperty("conversationId").GetGuid();
    }

    [Fact]
    public async Task List_ReturnsOnlyMyConversationsNewestFirst()
    {
        var me = await MemberAsync();
        var first = await AskAsync(me, "First question?", "One.");
        var second = await AskAsync(me, "Second question?", "Two.");
        await AskAsync(await MemberAsync(), "Not mine?", "Three.");

        var list = await me.GetFromJsonAsync<JsonElement>("/api/assistant/conversations");

        list.EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).Should().Equal(second, first);
        list[0].GetProperty("title").GetString().Should().Be("Second question?");
    }

    [Fact]
    public async Task Get_ReturnsTheMessages()
    {
        var me = await MemberAsync();
        var id = await AskAsync(me, "Where is my order?", "It shipped.");

        var conversation = await me.GetFromJsonAsync<JsonElement>($"/api/assistant/conversations/{id}");

        var messages = conversation.GetProperty("messages").EnumerateArray().ToList();
        messages.Select(m => m.GetProperty("role").GetString()).Should().Equal("user", "assistant");
        messages[1].GetProperty("text").GetString().Should().Be("It shipped.");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task AnotherUsersConversation_IsNotFound(string method)
    {
        var id = await AskAsync(await MemberAsync(), "Mine?", "Yes.");
        var stranger = await MemberAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/assistant/conversations/{id}")
        {
            Content = string.Equals(method, "PATCH", StringComparison.Ordinal) ? JsonContent.Create(new { title = "Stolen" }) : null,
        };

        var response = await stranger.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rename_ChangesTheTitle()
    {
        var me = await MemberAsync();
        var id = await AskAsync(me, "Old title?", "Ok.");

        var response = await me.PatchAsJsonAsync($"/api/assistant/conversations/{id}", new { title = "  New title  " });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await me.GetFromJsonAsync<JsonElement>($"/api/assistant/conversations/{id}")).GetProperty("title").GetString().Should().Be("New title");
    }

    [Fact]
    public async Task Rename_BlankTitle_IsBadRequest()
    {
        var me = await MemberAsync();
        var id = await AskAsync(me, "Title?", "Ok.");

        var response = await me.PatchAsJsonAsync($"/api/assistant/conversations/{id}", new { title = "  " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_RemovesIt()
    {
        var me = await MemberAsync();
        var id = await AskAsync(me, "Forget me?", "Ok.");

        (await me.DeleteAsync($"/api/assistant/conversations/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await me.GetAsync($"/api/assistant/conversations/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_WhenDisabled_IsNotFound()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        (await client.GetAsync("/api/assistant/conversations")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_Anonymous_IsUnauthorized()
    {
        (await _enabled.CreateClient().GetAsync("/api/assistant/conversations")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
