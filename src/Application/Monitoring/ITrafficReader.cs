namespace AiFramework.Application.Monitoring;

/// <summary>
/// Reads what <see cref="Abstractions.ITrafficRecorder"/> flushed.
/// </summary>
/// <remarks>
/// Every method here SUMS ACROSS INSTANCES. That is not an implementation detail: a row is one
/// pod's minute, and reading one pod's rows would report whichever replica the operator's own
/// session is pinned to rather than the application's traffic (ADR 0010, ADR 0018).
/// </remarks>
public interface ITrafficReader
{
    public Task<TrafficSummaryView> SummarizeAsync(
        DateTimeOffset since, CancellationToken cancellationToken);

    public Task<TrafficSeriesView> SeriesAsync(
        DateTimeOffset since, CancellationToken cancellationToken);
}
