using AiFramework.Domain.Users;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Persistence;

[Collection(nameof(PostgresCollection))]
public sealed class UserRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static string AUniqueName() => $"ada{Guid.NewGuid():N}"[..User.MaxUsernameLength];

    [Fact]
    public async Task AddAsync_ThenSaveChanges_PersistsTheUser()
    {
        var id = Guid.NewGuid();
        var username = AUniqueName();

        await using (var context = fixture.CreateContext())
        {
            await new UserRepository(context).AddAsync(
                User.Register(id, username, "hash", "Ada Lovelace", RegisteredAt), CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new UserRepository(verify).GetAsync(id, CancellationToken.None);

        found.Should().NotBeNull();
        found.Username.Should().Be(username);
        found.PasswordHash.Should().Be("hash");
        found.DisplayName.Should().Be("Ada Lovelace");
        found.RegisteredAt.Should().Be(RegisteredAt);
    }

    [Fact]
    public async Task GetByNormalizedUsernameAsync_FindsAUserWhateverCaseTheCallerTyped()
    {
        var username = AUniqueName();

        await using (var context = fixture.CreateContext())
        {
            await new UserRepository(context).AddAsync(
                User.Register(Guid.NewGuid(), username, "hash", "Ada Lovelace", RegisteredAt),
                CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new UserRepository(verify)
            .GetByNormalizedUsernameAsync(User.Normalize(username.ToUpperInvariant()), CancellationToken.None);

        found.Should().NotBeNull();
        found.Username.Should().Be(username);
    }

    [Fact]
    public async Task GetByNormalizedUsernameAsync_WhenNobodyMatches_ReturnsNull()
    {
        await using var context = fixture.CreateContext();

        var found = await new UserRepository(context)
            .GetByNormalizedUsernameAsync(User.Normalize(AUniqueName()), CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task SaveChanges_WithAUsernameDifferingOnlyByCase_ViolatesTheUniqueIndex()
    {
        var username = AUniqueName();

        await using (var context = fixture.CreateContext())
        {
            await new UserRepository(context).AddAsync(
                User.Register(Guid.NewGuid(), username, "hash", "The First Ada", RegisteredAt),
                CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var second = fixture.CreateContext();
        await new UserRepository(second).AddAsync(
            User.Register(Guid.NewGuid(), username.ToUpperInvariant(), "hash", "The Second Ada", RegisteredAt),
            CancellationToken.None);

        var act = async () => await new UnitOfWork(second).SaveChangesAsync(CancellationToken.None);

        // The database is the real guard, not the check in RegisterUserHandler: two simultaneous
        // registrations of the same name both pass that check, and this is what stops the second.
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task GetAsync_ReturnsATrackedUser_SoAChangedPasswordIsSaved()
    {
        var id = Guid.NewGuid();

        await using (var context = fixture.CreateContext())
        {
            await new UserRepository(context).AddAsync(
                User.Register(id, AUniqueName(), "old-hash", "Ada Lovelace", RegisteredAt),
                CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using (var mutate = fixture.CreateContext())
        {
            var user = await new UserRepository(mutate).GetAsync(id, CancellationToken.None);
            user!.ChangePassword("new-hash");
            await new UnitOfWork(mutate).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var reloaded = await new UserRepository(verify).GetAsync(id, CancellationToken.None);

        // An AsNoTracking read here would leave the new hash unsaved, silently and with no error.
        reloaded!.PasswordHash.Should().Be("new-hash");
    }
}
