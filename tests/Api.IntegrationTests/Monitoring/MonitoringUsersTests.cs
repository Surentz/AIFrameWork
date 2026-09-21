using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiFramework.Api.Monitoring;
using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Monitoring;

/// <summary>
/// User administration over real HTTP, against the real policy and the real rails. See ADR 0022.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class MonitoringUsersTests(ApiFactory factory)
{
    private static readonly Uri Users = new("/api/monitoring/users", UriKind.Relative);

    /// <summary>
    /// Enums cross the wire as NAMES, not integers — see the Notifications section of the root
    /// CLAUDE.md. Deserializing with the framework default would read "Promoted" as invalid, so
    /// these options mirror what the API actually serializes, and an assertion here would notice
    /// if that convention ever regressed.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ApiFactory _factory = factory;

    private static Uri RoleOf(Guid id) =>
        new($"/api/monitoring/users/{id}/role", UriKind.Relative);

    private static Uri SignOutOf(Guid id) =>
        new($"/api/monitoring/users/{id}/sign-out", UriKind.Relative);

    private static Uri ActionsOf(Guid id) =>
        new($"/api/monitoring/users/{id}/actions", UriKind.Relative);

    /// <summary>Registers somebody for an administrator to act on, and returns their id.</summary>
    private async Task<Guid> ASubjectAsync()
    {
        var (_, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        return await _factory.GetUserIdAsync(username);
    }

    [Fact]
    public async Task AMember_IsRefusedEveryEndpoint()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var subject = await ASubjectAsync();

        var list = await client.GetAsync(Users);
        var role = await client.PostAsJsonAsync(
            RoleOf(subject), new ChangeUserRoleRequest { Role = UserRole.Admin });
        var signOut = await client.PostAsync(SignOutOf(subject), content: null);

        // The gate is the policy on the controller, not anything the screen does.
        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        role.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        signOut.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnAdministrator_CanListUsers()
    {
        var client = await _factory.CreateAdminClientAsync();
        await ASubjectAsync();

        var response = await client.GetAsync(Users);
        var page = await response.Content.ReadFromJsonAsync<AdministeredUserPageResponse>(Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // "Contains", never "equals": this project shares one database across its whole run.
        page!.Items.Should().NotBeEmpty();
        page.TotalCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AnAdministrator_CanPromoteAndThenDemoteSomebodyElse()
    {
        var client = await _factory.CreateAdminClientAsync();
        var subject = await ASubjectAsync();

        var promote = await client.PostAsJsonAsync(
            RoleOf(subject), new ChangeUserRoleRequest { Role = UserRole.Admin });
        promote.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _factory.GetRoleAsync(subject)).Should().Be(UserRole.Admin);

        var demote = await client.PostAsJsonAsync(
            RoleOf(subject), new ChangeUserRoleRequest { Role = UserRole.Member });
        demote.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _factory.GetRoleAsync(subject)).Should().Be(UserRole.Member);
    }

    [Fact]
    public async Task AnAdministrator_DemotingThemselves_IsAConflict()
    {
        var (client, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        await _factory.SetRoleAsync(username, UserRole.Admin);
        var self = await _factory.GetUserIdAsync(username);

        var response = await client.PostAsJsonAsync(
            RoleOf(self), new ChangeUserRoleRequest { Role = UserRole.Member });

        // The first rail, enforced by the server rather than by a hidden button.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _factory.GetRoleAsync(self)).Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task AnAdministrator_SigningThemselvesOutFromHere_IsAConflict()
    {
        var (client, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        await _factory.SetRoleAsync(username, UserRole.Admin);
        var self = await _factory.GetUserIdAsync(username);

        var response = await client.PostAsync(SignOutOf(self), content: null);

        // sign-out-everywhere on your own account is a real thing, through the endpoint that
        // exists for it. Doing it by misclick from a table of every account is not.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ARoleChange_DoesNotEndTheTargetsSessions()
    {
        var client = await _factory.CreateAdminClientAsync();
        var (subjectClient, subjectName) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        var subject = await _factory.GetUserIdAsync(subjectName);

        await client.PostAsJsonAsync(RoleOf(subject), new ChangeUserRoleRequest { Role = UserRole.Admin });

        // ADR 0020 and ADR 0022 both turn on this. The role is read from the database on every
        // request, so the change lands on their next one - while rotating the stamp would sign
        // them out of a session they are perfectly entitled to hold.
        var stillSignedIn = await subjectClient.GetAsync(new Uri("/api/auth/me", UriKind.Relative));
        stillSignedIn.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SigningAUserOut_EndsTheirSessions()
    {
        var client = await _factory.CreateAdminClientAsync();
        var (subjectClient, subjectName) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        var subject = await _factory.GetUserIdAsync(subjectName);

        var response = await client.PostAsync(SignOutOf(subject), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Rotating the stamp invalidates every cookie already issued for that account (ADR 0011),
        // and the validator reads it uncached on every request - so this takes effect at once.
        var afterwards = await subjectClient.GetAsync(new Uri("/api/auth/me", UriKind.Relative));
        afterwards.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EveryActionIsAudited_WithTheActorAndTheTarget()
    {
        var (client, actorName) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        await _factory.SetRoleAsync(actorName, UserRole.Admin);
        var subject = await ASubjectAsync();

        await client.PostAsJsonAsync(RoleOf(subject), new ChangeUserRoleRequest { Role = UserRole.Admin });
        await client.PostAsync(SignOutOf(subject), content: null);

        var response = await client.GetAsync(ActionsOf(subject));
        var page = await response.Content.ReadFromJsonAsync<AdminActionPageResponse>(Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        page!.Items.Should().HaveCount(2);
        page.Items.Should().OnlyContain(a => a.TargetUserId == subject);
        page.Items.Select(a => a.Kind).Should()
            .BeEquivalentTo([AdminActionKind.SignedOutEverywhere, AdminActionKind.Promoted]);
        // Denormalized on purpose: the record stays readable whatever happens to either account.
        page.Items.Should().OnlyContain(a => a.ActorUsername == actorName);
    }

    [Fact]
    public async Task AnAuditRowIsNotWritten_WhenTheChangeIsRefused()
    {
        var (client, actorName) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        await _factory.SetRoleAsync(actorName, UserRole.Admin);
        var self = await _factory.GetUserIdAsync(actorName);

        await client.PostAsJsonAsync(RoleOf(self), new ChangeUserRoleRequest { Role = UserRole.Member });

        var response = await client.GetAsync(ActionsOf(self));
        var page = await response.Content.ReadFromJsonAsync<AdminActionPageResponse>(Json);

        // An audit recording changes that did not happen is worse than no audit at all.
        page!.Items.Should().BeEmpty();
    }
}
