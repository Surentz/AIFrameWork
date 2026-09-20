using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>Traffic over a trailing window, whole and per endpoint or handler.</summary>
public sealed record GetTrafficSummary(TimeSpan Window) : IQuery<TrafficSummaryView>;

public sealed class GetTrafficSummaryHandler(ITrafficReader traffic, IClock clock)
    : IQueryHandler<GetTrafficSummary, TrafficSummaryView>
{
    public async Task<Result<TrafficSummaryView>> HandleAsync(
        GetTrafficSummary query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Window <= TimeSpan.Zero || query.Window > GetJobHealthHandler.MaxWindow)
        {
            return Result.Failure<TrafficSummaryView>(new Error(
                ErrorKind.Validation,
                "monitoring.window_invalid",
                $"The window must be positive and no more than {GetJobHealthHandler.MaxWindow.TotalDays} days."));
        }

        var summary = await traffic
            .SummarizeAsync(clock.UtcNow - query.Window, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(summary);
    }
}

/// <summary>The per-minute series behind the traffic chart.</summary>
public sealed record GetTrafficSeries(TimeSpan Window) : IQuery<TrafficSeriesView>;

public sealed class GetTrafficSeriesHandler(ITrafficReader traffic, IClock clock)
    : IQueryHandler<GetTrafficSeries, TrafficSeriesView>
{
    /// <summary>
    /// A day of minutes is 1,440 points, which is already more than a chart can show. Beyond that
    /// the query is asking for an archive rather than a picture.
    /// </summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(1);

    public async Task<Result<TrafficSeriesView>> HandleAsync(
        GetTrafficSeries query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Window <= TimeSpan.Zero || query.Window > MaxWindow)
        {
            return Result.Failure<TrafficSeriesView>(new Error(
                ErrorKind.Validation,
                "monitoring.window_invalid",
                $"The series window must be positive and no more than {MaxWindow.TotalHours} hours."));
        }

        var series = await traffic
            .SeriesAsync(clock.UtcNow - query.Window, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(series);
    }
}
