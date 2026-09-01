using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Outbox;

[Collection(nameof(PostgresCollection))]
public sealed class OutboxPollerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    // CreatePoller hands the DbContext's ownership to this list rather than disposing it
    // inline: the poller needs the context to stay open for the life of the test, so nothing
    // in CreatePoller itself can dispose it. Tracking it here (disposed in DisposeAsync) is
    // what satisfies CA2000 without leaking a connection past the test.
    private readonly List<AiFrameworkDbContext> _createdContexts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var context in _createdContexts)
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }

    private OutboxPoller CreatePoller(TestClock clock, OutboxOptions? options = null)
    {
        var context = fixture.CreateContext();
        _createdContexts.Add(context);
        return new OutboxPoller(context, Options.Create(options ?? new OutboxOptions()), clock);
    }

    private async Task<Guid> SeedAsync(OutboxStatus status, DateTimeOffset? nextAttemptAt = null,
        DateTimeOffset? leasedUntil = null, DateTimeOffset? processedAt = null)
    {
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        context.Outbox.Add(new OutboxMessage
        {
            Id = id,
            EventName = "test.event",
            Payload = "{}",
            OccurredAt = Now,
            Status = status,
            NextAttemptAt = nextAttemptAt,
            LeasedUntil = leasedUntil,
            ProcessedAt = processedAt,
        });
        await context.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ClaimAsync_ClaimsADuePendingRow()
    {
        var id = await SeedAsync(OutboxStatus.Pending);
        var poller = CreatePoller(new TestClock(Now));

        var claimed = await poller.ClaimAsync(CancellationToken.None);

        claimed.Should().ContainSingle(i => i.Id == id).Which.Attempt.Should().Be(1);

        await using var verify = fixture.CreateContext();
        var row = await verify.Outbox.SingleAsync(m => m.Id == id);
        row.Status.Should().Be(OutboxStatus.InFlight);
        row.LeasedUntil.Should().NotBeNull();
    }

    [Fact]
    public async Task ClaimAsync_SkipsARowWhoseBackoffHasNotElapsed()
    {
        var id = await SeedAsync(OutboxStatus.Pending, nextAttemptAt: Now.AddMinutes(5));
        var poller = CreatePoller(new TestClock(Now));

        var claimed = await poller.ClaimAsync(CancellationToken.None);

        // Scoped to the seeded row, not claimed.Should().BeEmpty(): the shared container is
        // never truncated between test classes, and OutboxAtomicityTests leaves Pending rows
        // behind permanently. Asserting global emptiness only held because another test in
        // this class (with a large BatchSize) happened to run first and sweep those stray
        // rows away — reorder the methods and that assumption breaks. This assertion only
        // cares whether THIS row was claimed, which is what the test is actually about.
        claimed.Should().NotContain(i => i.Id == id);
    }

    [Fact]
    public async Task ClaimAsync_ReclaimsAnInFlightRowWhoseLeaseExpired()
    {
        var id = await SeedAsync(OutboxStatus.InFlight, leasedUntil: Now.AddMinutes(-1));
        var poller = CreatePoller(new TestClock(Now));

        var claimed = await poller.ClaimAsync(CancellationToken.None);

        claimed.Should().ContainSingle(i => i.Id == id);
    }

    [Fact]
    public async Task ClaimAsync_LeavesAProcessedRowAlone()
    {
        var id = await SeedAsync(OutboxStatus.Processed, processedAt: Now);
        var poller = CreatePoller(new TestClock(Now));

        var claimed = await poller.ClaimAsync(CancellationToken.None);

        // Scoped to the seeded row rather than claimed.Should().BeEmpty() — see the comment
        // in ClaimAsync_SkipsARowWhoseBackoffHasNotElapsed. The shared, never-truncated
        // container can hold stray Pending rows left by other test classes; this test only
        // asserts that a Processed row is not among whatever gets claimed.
        claimed.Should().NotContain(i => i.Id == id);
    }

    [Fact]
    public async Task ClaimAsync_WithTwoConcurrentPollers_ReturnsDisjointSets()
    {
        // Seed strictly more rows than a single BatchSize can take. With exactly BatchSize
        // rows seeded, a poller that simply wins the race and grabs everything before the
        // other's SELECT runs also produces an empty/empty split - disjoint, but vacuous,
        // since it can't tell "SKIP LOCKED skipped locked rows" apart from "there was never
        // any contention." Seeding 20 against BatchSize = 10 forces both outcomes (a genuine
        // interleave, or a clean serialization) to leave both pollers non-empty, so the
        // disjointness assertion below is only satisfiable if SKIP LOCKED actually worked.
        // Do not shrink this back to BatchSize rows - it would silently remove the test's
        // meaning while still showing green.
        for (var i = 0; i < 20; i++)
        {
            await SeedAsync(OutboxStatus.Pending);
        }

        var first = CreatePoller(new TestClock(Now), new OutboxOptions { BatchSize = 10 });
        var second = CreatePoller(new TestClock(Now), new OutboxOptions { BatchSize = 10 });

        var results = await Task.WhenAll(
            first.ClaimAsync(CancellationToken.None),
            second.ClaimAsync(CancellationToken.None));

        results[0].Should().HaveCount(10, "20 due rows and BatchSize 10 means a working claim must exhaust the first poller's batch");
        results[1].Should().HaveCount(10, "the remaining 10 rows must go to the second poller, not vanish or double up");
        results[0].Select(i => i.Id).Intersect(results[1].Select(i => i.Id))
            .Should().BeEmpty("FOR UPDATE SKIP LOCKED must prevent double-claiming");
    }

    [Fact]
    public async Task PruneAsync_DeletesProcessedRowsPastRetentionButKeepsDeadOnes()
    {
        var old = await SeedAsync(OutboxStatus.Processed, processedAt: Now.AddDays(-30));
        var dead = await SeedAsync(OutboxStatus.Dead, processedAt: Now.AddDays(-30));
        var poller = CreatePoller(new TestClock(Now));

        await poller.PruneAsync(CancellationToken.None);

        await using var verify = fixture.CreateContext();
        (await verify.Outbox.AnyAsync(m => m.Id == old)).Should().BeFalse();
        (await verify.Outbox.AnyAsync(m => m.Id == dead)).Should().BeTrue();
    }
}
