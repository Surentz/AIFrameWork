using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Statistics;

/// <summary>An area's population on the first day of a quarter, as the statistics office published it.</summary>
public sealed record AreaPopulation(string AreaCode, string AreaName, string Period, long Population);

/// <summary>An area population figures are published for: all of Denmark, a region or a municipality.</summary>
public sealed record StatisticsArea(string Code, string Name);

/// <summary>
/// Official population figures. Statistics Denmark's StatBank implements it today (the external
/// systems pilot, ADR 0031); nothing here knows that. Fails with <see cref="ErrorKind.NotFound"/>
/// for an area or figure the source does not publish, and <see cref="ErrorKind.Unavailable"/>
/// when the source cannot be reached or answers outside its contract.
/// </summary>
public interface IPopulationStatistics
{
    public Task<Result<AreaPopulation>> GetLatestAsync(string areaCode, CancellationToken cancellationToken);

    public Task<Result<IReadOnlyList<StatisticsArea>>> GetAreasAsync(CancellationToken cancellationToken);
}
