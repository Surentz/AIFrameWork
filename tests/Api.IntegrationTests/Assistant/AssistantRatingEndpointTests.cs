using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Assistant;

[Collection(nameof(ApiFactoryCollection))]
public sealed class AssistantRatingEndpointTests(ApiFactory factory) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _quick = null!;

    public Task InitializeAsync()
    {
        _quick = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Assistant:Quick:Enabled", "true");
            b.UseSetting("RateLimiting:AssistantQuick:PermitLimit", "1000");
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _quick.DisposeAsync();

    private async Task<HttpClient> MemberAsync()
    {
        var client = _quick.CreateClient();
        (await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Username = $"m{Guid.NewGuid():N}"[..32], Password = ApiFactory.RegisteredPassword, DisplayName = "Member" })).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Asks one quick question and returns the done event: its conversation and the answer's turn.</summary>
    private static async Task<(Guid Conversation, int Turn)> AskAsync(HttpClient client)
    {
        var body = await (await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Has my last order shipped?" })).Content.ReadAsStringAsync();
        var done = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => JsonDocument.Parse(block.Split('\n').Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..]).RootElement)
            .Single(e => string.Equals(e.GetProperty("type").GetString(), "done", StringComparison.Ordinal));
        return (done.GetProperty("conversationId").GetGuid(), done.GetProperty("turn").GetInt32());
    }

    private static string RatingPath(Guid conversation, int turn) => $"/api/assistant/conversations/{conversation}/turns/{turn}/rating";

    [Fact]
    public async Task Rate_TheAnswer_IsNoContent()
    {
        var client = await MemberAsync();
        var (conversation, turn) = await AskAsync(client);

        var response = await client.PutAsJsonAsync(RatingPath(conversation, turn), new { Rating = "Down" });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Rate_TheQuestionTurn_IsBadRequest()
    {
        var client = await MemberAsync();
        var (conversation, turn) = await AskAsync(client);

        var response = await client.PutAsJsonAsync(RatingPath(conversation, turn - 1), new { Rating = "Up" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString().Should().Be("assistant.not_an_answer");
    }

    [Fact]
    public async Task Rate_AnotherUsersAnswer_IsNotFound()
    {
        var (conversation, turn) = await AskAsync(await MemberAsync());
        var stranger = await MemberAsync();

        var response = await stranger.PutAsJsonAsync(RatingPath(conversation, turn), new { Rating = "Up" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rate_Anonymously_IsUnauthorized()
    {
        var response = await _quick.CreateClient().PutAsJsonAsync(RatingPath(Guid.NewGuid(), 1), new { Rating = "Up" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
