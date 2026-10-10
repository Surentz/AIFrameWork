using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Monitoring;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Monitoring;

[Collection(nameof(PostgresCollection))]
public sealed class ExternalSystemStatusStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture = fixture;

    // Unique per test: the collection shares one database.
    private static string Unique(string name) => $"{name}-{Guid.NewGuid():N}";

    private static ExternalSystemStatusView Row(string name, ExternalSystemState state, DateTimeOffset at) =>
        new(name, state, $"{name} is {state}", at, at.AddDays(90), TokenOk: true);

    [Fact]
    public async Task ReplaceAsync_ThenListAsync_ReturnsWhatWasWritten()
    {
        var name = Unique("partner");
        await using var context = _fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);

        await store.ReplaceAsync([Row(name, ExternalSystemState.Degraded, At)], [name], CancellationToken.None);

        var rows = await store.ListAsync(CancellationToken.None);
        rows.Should().ContainSingle(r => string.Equals(r.Name, name, StringComparison.Ordinal))
            .Which.Should().Be(Row(name, ExternalSystemState.Degraded, At));
    }

    [Fact]
    public async Task ReplaceAsync_WithNonUtcOffsets_StoresTheSameInstants()
    {
        var name = Unique("partner");
        var checkedAt = new DateTimeOffset(2026, 8, 30, 14, 0, 0, TimeSpan.FromHours(2));
        var notAfter = new DateTimeOffset(2026, 11, 30, 3, 30, 0, TimeSpan.FromHours(-5));
        await using var context = _fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);

        await store.ReplaceAsync(
            [Row(name, ExternalSystemState.Healthy, checkedAt) with { CertificateNotAfter = notAfter }],
            [name], CancellationToken.None);

        var row = (await store.ListAsync(CancellationToken.None))
            .Single(r => string.Equals(r.Name, name, StringComparison.Ordinal));
        row.CheckedAt.Should().Be(checkedAt);
        row.CertificateNotAfter.Should().Be(notAfter);
    }

    [Fact]
    public async Task ReplaceAsync_CalledTwiceForTheSameSystem_KeepsOneRowWithTheLatestValues()
    {
        var name = Unique("partner");
        await using var context = _fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);

        await store.ReplaceAsync([Row(name, ExternalSystemState.Healthy, At)], [name], CancellationToken.None);
        await store.ReplaceAsync([Row(name, ExternalSystemState.Unhealthy, At.AddMinutes(1))], [name], CancellationToken.None);

        var rows = await store.ListAsync(CancellationToken.None);
        rows.Where(r => string.Equals(r.Name, name, StringComparison.Ordinal)).Should().ContainSingle()
            .Which.State.Should().Be(ExternalSystemState.Unhealthy);
    }

    [Fact]
    public async Task ReplaceAsync_RemovesRowsForSystemsNoLongerConfigured()
    {
        var kept = Unique("kept");
        var removed = Unique("removed");
        await using var context = _fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);
        await store.ReplaceAsync(
            [Row(kept, ExternalSystemState.Healthy, At), Row(removed, ExternalSystemState.Healthy, At)],
            [kept, removed],
            CancellationToken.None);

        await store.ReplaceAsync([Row(kept, ExternalSystemState.Healthy, At)], [kept], CancellationToken.None);

        var names = (await store.ListAsync(CancellationToken.None)).Select(r => r.Name).ToList();
        names.Should().Contain(kept).And.NotContain(removed);
    }

    [Fact]
    public async Task ReplaceAsync_WithAnEmptyReportAndAConfiguredSystem_LeavesItsRowInPlace()
    {
        var name = Unique("partner");
        await using var context = _fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);
        await store.ReplaceAsync([Row(name, ExternalSystemState.Healthy, At)], [name], CancellationToken.None);

        await store.ReplaceAsync([], [name], CancellationToken.None);

        var rows = await store.ListAsync(CancellationToken.None);
        rows.Select(r => r.Name).Should().Contain(name);
    }

    [Fact]
    public async Task ReplaceAsync_WithAnEmptyReportAndNothingConfigured_DeletesEveryRow()
    {
        var name = Unique("partner");
        await using var context = _fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);
        await store.ReplaceAsync([Row(name, ExternalSystemState.Healthy, At)], [name], CancellationToken.None);

        await store.ReplaceAsync([], [], CancellationToken.None);

        (await store.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReplaceAsync_WithAnEmptyReport_DeletesOnlyRowsNoConfiguredNameMatchesIgnoringCase()
    {
        var name = Unique("Partner");
        var stale = Unique("stale");
        await using var context = _fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);
        await store.ReplaceAsync(
            [Row(name, ExternalSystemState.Healthy, At), Row(stale, ExternalSystemState.Healthy, At)],
            [name, stale],
            CancellationToken.None);

        await store.ReplaceAsync([], [name.ToUpperInvariant()], CancellationToken.None);

        var names = (await store.ListAsync(CancellationToken.None)).Select(r => r.Name).ToList();
        names.Should().Contain(name).And.NotContain(stale);
    }

    [Fact]
    public async Task ReplaceAsync_TruncatesALongDescription()
    {
        var name = Unique("partner");
        await using var context = _fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);

        await store.ReplaceAsync(
            [Row(name, ExternalSystemState.Unhealthy, At) with { Description = new string('x', 2000) }],
            [name], CancellationToken.None);

        var row = (await store.ListAsync(CancellationToken.None)).Single(r => string.Equals(r.Name, name, StringComparison.Ordinal));
        row.Description.Should().HaveLength(ExternalSystemStatusStore.MaxDescriptionLength);
    }
}
