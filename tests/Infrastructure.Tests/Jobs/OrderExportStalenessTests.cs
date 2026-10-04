using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Jobs;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Jobs;

/// <summary>
/// Ties <see cref="OrderExport.StaleAfter"/> to the job retry schedule. It lives here, not in
/// Domain.Tests, because the schedule is Infrastructure's and the domain cannot see it.
/// </summary>
/// <remarks>
/// An export reads as failed once StaleAfter has passed. If that came before the last retry, the
/// screen would say Failed while a retry was still coming, and "Try again" would start a second
/// build beside it. Lengthening the retry schedule without StaleAfter fails here.
/// </remarks>
public sealed class OrderExportStalenessTests
{
    [Fact]
    public void StaleAfter_OutlastsEveryJobRetry()
    {
        var lastRetry = JobRegistration.JobRetryDelays.Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay);

        OrderExport.StaleAfter.Should().BeGreaterThan(lastRetry);
    }
}
