using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Persistence;

/// <summary>
/// The 7-day sweep of exported order histories. Personal data, so this is a requirement rather
/// than housekeeping (ADR 0029).
/// </summary>
/// <remarks>
/// Dated far beyond every other test's rows. The sweep is a set-based delete with no owner filter,
/// so it also removes other classes' older rows — harmless, since each test seeds and reads its
/// own within one method, but the reason no assertion here counts what was deleted.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class OrderExportRetentionTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2300, 1, 8, 12, 0, 0, TimeSpan.Zero);

    private async Task<Guid> SeedAsync(DateTimeOffset requestedAt)
    {
        var export = OrderExport.Request(Guid.NewGuid(), Guid.NewGuid(), requestedAt);
        export.ClearDomainEvents();
        await using var context = fixture.CreateContext();
        context.OrderExports.Add(export);
        await context.SaveChangesAsync();
        return export.Id;
    }

    private async Task<bool> ExistsAsync(Guid id)
    {
        await using var context = fixture.CreateContext();
        return await context.OrderExports.AnyAsync(e => e.Id == id);
    }

    private async Task PruneAsync(int retentionDays)
    {
        await using var context = fixture.CreateContext();
        var retention = new OrderExportRetention(
            context, Options.Create(new OrderExportOptions { RetentionDays = retentionDays }), new TestClock(Now));
        await retention.PruneAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PruneAsync_DeletesAnExportOlderThanTheRetention()
    {
        var old = await SeedAsync(Now.AddDays(-7).AddMinutes(-1));

        await PruneAsync(retentionDays: 7);

        (await ExistsAsync(old)).Should().BeFalse();
    }

    [Fact]
    public async Task PruneAsync_KeepsAnExportInsideTheRetention()
    {
        var recent = await SeedAsync(Now.AddDays(-7).AddMinutes(1));

        await PruneAsync(retentionDays: 7);

        (await ExistsAsync(recent)).Should().BeTrue();
    }

    [Fact]
    public void RetentionDays_DefaultsToSeven()
    {
        new OrderExportOptions().RetentionDays.Should().Be(7);
    }

    /// <summary>
    /// Zero would put the sweep's cutoff at "now" and delete every export, the ones still being
    /// built included; a negative value, every export ever. Refused at startup rather than obeyed.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithLessThanOneDay_Fails(int retentionDays)
    {
        var result = new OrderExportOptionsValidator()
            .Validate(null, new OrderExportOptions { RetentionDays = retentionDays });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("OrderExports:RetentionDays");
    }

    [Fact]
    public void Validate_WithOneDayOrMore_Succeeds()
    {
        new OrderExportOptionsValidator()
            .Validate(null, new OrderExportOptions { RetentionDays = 1 })
            .Succeeded.Should().BeTrue();
    }
}
