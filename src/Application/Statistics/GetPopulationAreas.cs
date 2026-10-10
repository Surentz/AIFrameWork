using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Statistics;

public sealed record PopulationAreaView(string Code, string Name);

public sealed record PopulationAreasView(IReadOnlyList<PopulationAreaView> Areas, string Source);

/// <summary>Every area population figures are published for, in the source's order. Cached for an hour.</summary>
public sealed record GetPopulationAreas : IQuery<PopulationAreasView>, ICacheable
{
    public string CacheKey => "all";

    public TimeSpan Duration => TimeSpan.FromHours(1);
}

public sealed class GetPopulationAreasHandler(IPopulationStatistics statistics)
    : IQueryHandler<GetPopulationAreas, PopulationAreasView>
{
    public async Task<Result<PopulationAreasView>> HandleAsync(GetPopulationAreas query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var result = await statistics.GetAreasAsync(cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Result.Success(new PopulationAreasView(
                [.. result.Value.Select(area => new PopulationAreaView(area.Code, area.Name))], GetPopulationHandler.Source))
            : Result.Failure<PopulationAreasView>(result.Error);
    }
}
