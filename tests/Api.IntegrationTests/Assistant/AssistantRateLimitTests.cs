extern alias simulator;

using System.Net;
using System.Net.Http.Json;
using AiFramework.Domain.Users;
using simulator::AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Assistant;

/// <summary>The 20-an-hour limit is per administrator: one admin's 429 never blocks another.</summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class AssistantRateLimitTests(ApiFactory factory) : IAsyncLifetime
{
    private readonly string _keyFile = Path.GetTempFileName();
    private FakeMessagesApi _fake = null!;
    private WebApplicationFactory<Program> _limited = null!;

    public async Task InitializeAsync()
    {
        await File.WriteAllTextAsync(_keyFile, "sk-test");
        _fake = await FakeMessagesApi.StartAsync();
        _limited = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Assistant:Enabled", "true");
            b.UseSetting("Assistant:ApiKeyFile", _keyFile);
            b.UseSetting("Assistant:BaseUrl", _fake.BaseAddress.ToString().TrimEnd('/'));
            b.UseSetting("RateLimiting:Assistant:PermitLimit", "1");
        });
    }

    public async Task DisposeAsync()
    {
        await _limited.DisposeAsync();
        await _fake.DisposeAsync();
        File.Delete(_keyFile);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = _limited.CreateClient();
        var username = $"r{Guid.NewGuid():N}"[..32];
        (await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Username = username, Password = ApiFactory.RegisteredPassword, DisplayName = "Admin" })).EnsureSuccessStatusCode();
        await factory.SetRoleAsync(username, UserRole.Admin);
        return client;
    }

    [Fact]
    public async Task Ask_OverTheLimit_IsTooManyRequestsForThatAdminOnly()
    {
        var first = await AdminAsync();
        var second = await AdminAsync();

        (await first.PostAsJsonAsync("/api/assistant/messages", new { question = "1" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.PostAsJsonAsync("/api/assistant/messages", new { question = "2" })).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await second.PostAsJsonAsync("/api/assistant/messages", new { question = "1" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
