using System.Net;
using System.Net.Http.Json;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Api.IntegrationTests;

/// <summary>
/// The key ring must live in Postgres, not in each host's memory: two API replicas that
/// disagree about keys reject each other's session cookies, which presents as an
/// intermittent 401 rather than as an obvious failure.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class DataProtectionTests(ApiFactory factory)
{
    /// <remarks>
    /// Asserting on the table rather than on cross-host cookie acceptance is deliberate.
    /// With no store configured, Data Protection falls back to a filesystem key ring keyed
    /// by content-root path, which both test hosts share - so a cookie test passes here
    /// even with the bug present, and only fails once the hosts are separate containers.
    /// </remarks>
    [Fact]
    public async Task IssuingACookie_PersistsTheKeyRingToTheDatabase()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        client.Dispose();

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        var keyCount = await context.DataProtectionKeys.AsNoTracking().CountAsync();

        keyCount.Should().BeGreaterThan(0);
    }

    /// <remarks>
    /// A second and weaker guard than the sibling test above, despite the stronger-sounding
    /// name: both hosts run in this process and share a content root, so the filesystem key
    /// ring Data Protection falls back to is shared too, and this passes on one machine even
    /// with PersistKeysToDbContext removed. The database assertion is what actually holds.
    /// <para>
    /// Registers by hand rather than through CreateAuthenticatedClientAsync: that helper
    /// relies on the client's internal cookie handler, so the cookie never appears on
    /// DefaultRequestHeaders and cannot be lifted off it. Reading Set-Cookie from the
    /// response is the only way to carry the session to a second host.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACookieFromOneHost_IsAcceptedByASecondHost()
    {
        using var first = factory.CreateClient();

        var registration = await first.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                Username = $"u{Guid.NewGuid():N}"[..32],
                Password = "a long enough test password",
                DisplayName = "Test User",
            });
        registration.EnsureSuccessStatusCode();

        var setCookie = registration.Headers.GetValues("Set-Cookie").First();
        var sessionCookie = setCookie.Split(';', 2)[0];

        using var secondFactory = factory.WithWebHostBuilder(_ => { });
        using var second = secondFactory.CreateClient();
        second.DefaultRequestHeaders.Add("Cookie", sessionCookie);

        var response = await second.GetAsync("/api/orders");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
