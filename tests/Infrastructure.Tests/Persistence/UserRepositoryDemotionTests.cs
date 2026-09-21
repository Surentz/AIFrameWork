using AiFramework.Domain.Users;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Persistence;

/// <summary>
/// The last-administrator rail, against the real engine — because the whole reason it is written
/// as one conditional UPDATE rather than a read-then-write is a race that only a real database
/// can be shown to close. See ADR 0022 and <c>IUserRepository.TryDemoteAsync</c>.
/// </summary>
/// <remarks>
/// Every test here works against its own administrators and counts only those, because this
/// project shares one Postgres container across its whole run (see tests/CLAUDE.md) and other
/// classes leave users behind. "The last administrator" is therefore scoped to a marker in the
/// username rather than to the table.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class UserRepositoryDemotionTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static string AUniqueName() => $"adm{Guid.NewGuid():N}"[..User.MaxUsernameLength];

    /// <summary>Adds an administrator and returns their id.</summary>
    private async Task<Guid> AnAdministratorAsync()
    {
        var user = User.Register(Guid.NewGuid(), AUniqueName(), "hash", "Admin", RegisteredAt);
        user.ChangeRole(UserRole.Admin);

        await using var context = fixture.CreateContext();
        context.Users.Add(user);
        await context.SaveChangesAsync(CancellationToken.None);

        return user.Id;
    }

    private async Task<UserRole> RoleOfAsync(Guid id)
    {
        await using var context = fixture.CreateContext();
        return await context.Users.Where(u => u.Id == id).Select(u => u.Role).SingleAsync();
    }

    [Fact]
    public async Task TryDemoteAsync_WithAnotherAdministratorPresent_Demotes()
    {
        await AnAdministratorAsync();
        var target = await AnAdministratorAsync();

        await using var context = fixture.CreateContext();
        var demoted = await new UserRepository(context)
            .TryDemoteAsync(target, CancellationToken.None);

        demoted.Should().BeTrue();
        (await RoleOfAsync(target)).Should().Be(UserRole.Member);
    }

    [Fact]
    public async Task TryDemoteAsync_LandsImmediately_WithNoUnitOfWork()
    {
        await AnAdministratorAsync();
        var target = await AnAdministratorAsync();

        await using (var context = fixture.CreateContext())
        {
            await new UserRepository(context).TryDemoteAsync(target, CancellationToken.None);
            // Deliberately no SaveChangesAsync. ExecuteUpdateAsync issues its own statement, which
            // is what lets the rail be one round trip - and is the asymmetry the port documents.
        }

        (await RoleOfAsync(target)).Should().Be(UserRole.Member);
    }

    [Fact]
    public async Task TryDemoteAsync_OnAMemberAlready_ReportsFalse()
    {
        var member = User.Register(Guid.NewGuid(), AUniqueName(), "hash", "Member", RegisteredAt);

        await using (var seed = fixture.CreateContext())
        {
            seed.Users.Add(member);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = fixture.CreateContext();
        var demoted = await new UserRepository(context)
            .TryDemoteAsync(member.Id, CancellationToken.None);

        // Zero rows affected, because the WHERE clause also requires the row to BE an
        // administrator. The handler never reaches here for a no-op, but the statement must not
        // claim to have done something it did not.
        demoted.Should().BeFalse();
    }

    [Fact]
    public async Task TryDemoteAsync_WithNoOtherAdministrator_IsRefused()
    {
        await ClearAdministratorsAsync();
        var only = await AnAdministratorAsync();

        await using var context = fixture.CreateContext();
        var demoted = await new UserRepository(context).TryDemoteAsync(only, CancellationToken.None);

        // The rail itself, sequentially. Zero rows affected IS the refusal.
        demoted.Should().BeFalse();
        (await RoleOfAsync(only)).Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task TryDemoteAsync_WaitsOnTheDemotionAdvisoryLock()
    {
        // THE test for the concurrency design, and the only one here with any teeth on it.
        //
        // The honest history: this started as a single conditional UPDATE whose WHERE clause
        // carried an EXISTS over the other administrators. Two OVERLAPPING transactions demoting
        // DIFFERENT administrators each miss the other's uncommitted change, both pass the
        // EXISTS, and the table ends with nobody — demonstrated against real Postgres, not
        // theorised. A subquery read takes no locks, which is the whole reason.
        //
        // Two attempts to test that by racing the method against itself BOTH passed against the
        // broken version — two concurrent calls, then eight, because EF and Npgsql serialise them
        // in practice and the interleaving never happens on demand. A race test that cannot
        // observe the race is worse than none, so this asserts the MECHANISM instead: the method
        // waits on a lock that serialises every demotion, which is what makes the EXISTS safe.
        await ClearAdministratorsAsync();
        await AnAdministratorAsync();
        var target = await AnAdministratorAsync();

        // The same key the repository uses, taken from another connection and held open.
        await using var holder = fixture.CreateContext();
        await using var holding = await holder.Database.BeginTransactionAsync(CancellationToken.None);
        await holder.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({UserRepository.DemotionLockKey})", CancellationToken.None);

        await using var context = fixture.CreateContext();
        var demotion = new UserRepository(context).TryDemoteAsync(target, CancellationToken.None);

        var first = await Task.WhenAny(demotion, Task.Delay(TimeSpan.FromSeconds(2)));

        // Still waiting, because something else holds the lock. Remove the pg_advisory_xact_lock
        // from TryDemoteAsync and this line fails: the demotion completes immediately instead.
        first.Should().NotBeSameAs(demotion, "the demotion must wait for the lock to be released");

        // Released with the transaction, however it ends - which is the point of the xact variant.
        await holding.RollbackAsync(CancellationToken.None);

        (await demotion).Should().BeTrue();
        (await RoleOfAsync(target)).Should().Be(UserRole.Member);
    }

    /// <summary>
    /// This project shares one Postgres container across its whole run (see tests/CLAUDE.md), so
    /// administrators other classes left behind would satisfy the rail's condition and make these
    /// tests prove nothing.
    /// </summary>
    private async Task ClearAdministratorsAsync()
    {
        await using var context = fixture.CreateContext();
        await context.Users.Where(u => u.Role == UserRole.Admin)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Member),
                CancellationToken.None);
    }
}
