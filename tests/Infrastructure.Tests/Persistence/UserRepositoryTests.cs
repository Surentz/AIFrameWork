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
    public async Task TryAddAsync_WithAFreeUsername_PersistsTheUser()
    {
        var id = Guid.NewGuid();
        var username = AUniqueName();

        await using (var context = fixture.CreateContext())
        {
            await new UserRepository(context).TryAddAsync(
                User.Register(id, username, "hash", "Ada Lovelace", RegisteredAt), CancellationToken.None);
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
            await new UserRepository(context).TryAddAsync(
                User.Register(Guid.NewGuid(), username, "hash", "Ada Lovelace", RegisteredAt),
                CancellationToken.None);
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
    public async Task TryAddAsync_WithAUsernameDifferingOnlyByCase_ReturnsFalse()
    {
        var username = AUniqueName();

        await using (var context = fixture.CreateContext())
        {
            await new UserRepository(context).TryAddAsync(
                User.Register(Guid.NewGuid(), username, "hash", "The First Ada", RegisteredAt),
                CancellationToken.None);
        }

        await using var second = fixture.CreateContext();

        // The database is the real guard, not the check in RegisterUserHandler: two simultaneous
        // registrations of the same name both pass that check, and this is what stops the second -
        // as a false the handler turns into a 409, not an exception that becomes a 500.
        var added = await new UserRepository(second).TryAddAsync(
            User.Register(Guid.NewGuid(), username.ToUpperInvariant(), "hash", "The Second Ada", RegisteredAt),
            CancellationToken.None);

        added.Should().BeFalse();
    }

    [Fact]
    public async Task TryAddAsync_WhenTheUsernameIsTaken_LeavesNothingForTheUnitOfWorkToRetry()
    {
        var username = AUniqueName();
        await using (var context = fixture.CreateContext())
        {
            await new UserRepository(context).TryAddAsync(
                User.Register(Guid.NewGuid(), username, "hash", "The First Ada", RegisteredAt),
                CancellationToken.None);
        }

        await using var second = fixture.CreateContext();
        await new UserRepository(second).TryAddAsync(
            User.Register(Guid.NewGuid(), username, "hash", "The Second Ada", RegisteredAt),
            CancellationToken.None);

        // Detached on failure: were the loser still tracked as Added, any later commit in the same
        // request would replay the insert and throw after all.
        var saved = await new UnitOfWork(second).SaveChangesAsync(CancellationToken.None);

        saved.Should().Be(0);
    }

    [Fact]
    public async Task TryAddAsync_WithAFreeUsername_LeavesNothingForTheUnitOfWorkToCommit()
    {
        // TryAddAsync commits immediately, and RegisterUser's pipeline still runs the unit of
        // work's commit afterwards. That second commit must be empty, or one command becomes two
        // transactions - which is exactly what the "handlers never commit" rule exists to prevent.
        await using var context = fixture.CreateContext();
        await new UserRepository(context).TryAddAsync(
            User.Register(Guid.NewGuid(), AUniqueName(), "hash", "Ada Lovelace", RegisteredAt),
            CancellationToken.None);

        var saved = await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);

        saved.Should().Be(0);
    }

    [Fact]
    public async Task TryAddAsync_RacingTheSameUsername_AddsExactlyOne()
    {
        var username = AUniqueName();
        var contexts = Enumerable.Range(0, 8).Select(_ => fixture.CreateContext()).ToArray();

        try
        {
            var results = await Task.WhenAll(contexts.Select((context, i) =>
                new UserRepository(context).TryAddAsync(
                    User.Register(Guid.NewGuid(), username, "hash", $"Ada {i}", RegisteredAt),
                    CancellationToken.None)));

            // Whatever the interleaving, the losers report false rather than throwing.
            results.Count(added => added).Should().Be(1);
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task TryAddAsync_WhenAnotherConstraintIsViolated_StillThrows()
    {
        // Only a taken username is "taken". A duplicate primary key is a bug, and reporting it as
        // a 409 would hide it.
        var id = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            await new UserRepository(context).TryAddAsync(
                User.Register(id, AUniqueName(), "hash", "The First Ada", RegisteredAt),
                CancellationToken.None);
        }

        await using var second = fixture.CreateContext();
        var act = () => new UserRepository(second).TryAddAsync(
            User.Register(id, AUniqueName(), "hash", "Someone Else", RegisteredAt),
            CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task GetAsync_ReturnsATrackedUser_SoAChangedPasswordIsSaved()
    {
        var id = Guid.NewGuid();

        await using (var context = fixture.CreateContext())
        {
            await new UserRepository(context).TryAddAsync(
                User.Register(id, AUniqueName(), "old-hash", "Ada Lovelace", RegisteredAt),
                CancellationToken.None);
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

    /// <summary>
    /// The property that makes the whole lockout work: this writes WITHOUT a SaveChanges call.
    /// SignIn's failure path returns a failed Result, and Behaviors.CommitAsync does not commit
    /// those — a tracked mutation there would be discarded silently, and the counter would read
    /// zero forever.
    /// </summary>
    [Fact]
    public async Task TryRecordFailedSignInAsync_PersistsWithoutSaveChanges()
    {
        var id = Guid.NewGuid();
        var lockedUntil = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(id);

        bool recorded;
        await using (var write = fixture.CreateContext())
        {
            recorded = await new UserRepository(write).TryRecordFailedSignInAsync(
                id, expectedAttempts: 0, attempts: 5, lockedOutUntil: lockedUntil,
                rotatedSecurityStamp: null, CancellationToken.None);
            // Deliberately no SaveChangesAsync here.
        }

        await using var verify = fixture.CreateContext();
        var found = await new UserRepository(verify).GetAsync(id, CancellationToken.None);

        recorded.Should().BeTrue("nothing else had touched the row");
        found.Should().NotBeNull();
        found.FailedSignInAttempts.Should().Be(5);
        found.LockedOutUntil.Should().Be(lockedUntil);
    }

    /// <summary>
    /// The conditional half of the write, and the reason the method returns a bool at all. A
    /// caller's expectedAttempts is what it read before spending tens of milliseconds in PBKDF2;
    /// if a concurrent attempt has moved the counter since, this write must refuse rather than
    /// flatten it — otherwise N simultaneous guesses advance the counter by one between them.
    /// </summary>
    [Fact]
    public async Task TryRecordFailedSignInAsync_WithAStaleExpectation_RefusesAndLeavesTheRowAlone()
    {
        var id = Guid.NewGuid();
        var lockedUntil = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(id);

        await using (var winner = fixture.CreateContext())
        {
            await new UserRepository(winner).TryRecordFailedSignInAsync(
                id, expectedAttempts: 0, attempts: 1, lockedOutUntil: null,
                rotatedSecurityStamp: null, CancellationToken.None);
        }

        bool recorded;
        await using (var loser = fixture.CreateContext())
        {
            // Still believes the counter reads 0, exactly as a request that read before the
            // winner's UPDATE landed would.
            recorded = await new UserRepository(loser).TryRecordFailedSignInAsync(
                id, expectedAttempts: 0, attempts: 1, lockedOutUntil: lockedUntil,
                rotatedSecurityStamp: null, CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new UserRepository(verify).GetAsync(id, CancellationToken.None);

        recorded.Should().BeFalse("the row no longer holds the value this caller read");
        found.Should().NotBeNull();
        found.FailedSignInAttempts.Should().Be(1, "the winner's increment must survive");
        found.LockedOutUntil.Should().BeNull();
    }

    [Fact]
    public async Task ClearSignInFailuresAsync_ClearsTheCounterAndTheLockout()
    {
        var id = Guid.NewGuid();
        await SeedAsync(id);

        await using (var write = fixture.CreateContext())
        {
            var repository = new UserRepository(write);
            await repository.TryRecordFailedSignInAsync(
                id,
                expectedAttempts: 0,
                attempts: 5,
                new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero),
                rotatedSecurityStamp: null,
                CancellationToken.None);
            await repository.ClearSignInFailuresAsync(id, CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new UserRepository(verify).GetAsync(id, CancellationToken.None);

        found.Should().NotBeNull();
        found.FailedSignInAttempts.Should().Be(0);
        found.LockedOutUntil.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_RoundTripsTheSecurityStamp()
    {
        await using var context = fixture.CreateContext();
        var repository = new UserRepository(context);
        var ada = User.Register(Guid.NewGuid(), AUniqueName(), "hash", "Ada Lovelace", RegisteredAt);
        var stamp = ada.SecurityStamp;
        await repository.TryAddAsync(ada, CancellationToken.None);

        await using var reading = fixture.CreateContext();
        var found = await new UserRepository(reading).GetAsync(ada.Id, CancellationToken.None);

        found!.SecurityStamp.Should().Be(stamp);
    }

    [Fact]
    public async Task TryRecordFailedSignInAsync_WithARotatedStamp_WritesIt()
    {
        await using var context = fixture.CreateContext();
        var repository = new UserRepository(context);
        var ada = User.Register(Guid.NewGuid(), AUniqueName(), "hash", "Ada Lovelace", RegisteredAt);
        await repository.TryAddAsync(ada, CancellationToken.None);
        var original = ada.SecurityStamp;
        var rotated = Guid.NewGuid().ToString("N");

        var written = await repository.TryRecordFailedSignInAsync(
            ada.Id,
            expectedAttempts: 0,
            attempts: User.MaxFailedSignInAttempts,
            lockedOutUntil: DateTimeOffset.UtcNow.AddMinutes(15),
            rotatedSecurityStamp: rotated,
            CancellationToken.None);

        written.Should().BeTrue();

        await using var reading = fixture.CreateContext();
        var found = await new UserRepository(reading).GetAsync(ada.Id, CancellationToken.None);
        found!.SecurityStamp.Should().Be(rotated).And.NotBe(original);
    }

    [Fact]
    public async Task TryRecordFailedSignInAsync_WithoutARotatedStamp_LeavesTheStampAlone()
    {
        await using var context = fixture.CreateContext();
        var repository = new UserRepository(context);
        var ada = User.Register(Guid.NewGuid(), AUniqueName(), "hash", "Ada Lovelace", RegisteredAt);
        await repository.TryAddAsync(ada, CancellationToken.None);
        var original = ada.SecurityStamp;

        await repository.TryRecordFailedSignInAsync(
            ada.Id,
            expectedAttempts: 0,
            attempts: 1,
            lockedOutUntil: null,
            rotatedSecurityStamp: null,
            CancellationToken.None);

        await using var reading = fixture.CreateContext();
        var found = await new UserRepository(reading).GetAsync(ada.Id, CancellationToken.None);
        found!.SecurityStamp.Should().Be(original);
    }

    private async Task SeedAsync(Guid id)
    {
        await using var seed = fixture.CreateContext();
        await new UserRepository(seed).TryAddAsync(
            User.Register(id, $"u{id:N}"[..32], "hash", "Ada Lovelace", RegisteredAt),
            CancellationToken.None);
    }
}
