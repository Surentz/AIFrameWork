using System.Diagnostics.CodeAnalysis;
using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Statistics;

/// <summary>The latest published population of one area, with the attribution its licence requires.</summary>
public sealed record PopulationView(string AreaCode, string AreaName, string Period, long Population, string Source);

/// <summary>
/// The latest quarter's population for an area code (three digits: 000 is all of Denmark, 084 a
/// region, 101 a municipality). Cached for an hour, per caller (the caching behavior scopes every key
/// to its user, ADR 0009): the figure changes once a quarter, and every
/// miss is a call to a third party.
/// </summary>
public sealed record GetPopulation(string? Area) : IQuery<PopulationView>, ICacheable
{
    public string CacheKey => Area ?? string.Empty;

    public TimeSpan Duration => TimeSpan.FromHours(1);
}

/// <summary>
/// Validates its own input, because queries get no validation behavior: a malformed code is a
/// 400 and never a call to the source.
/// </summary>
public sealed class GetPopulationHandler(IPopulationStatistics statistics) : IQueryHandler<GetPopulation, PopulationView>
{
    /// <summary>Statistics Denmark's terms (CC BY 4.0) require the source to travel with the figure.</summary>
    public const string Source = "Statistics Denmark (CC BY 4.0)";

    public async Task<Result<PopulationView>> HandleAsync(GetPopulation query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!IsAreaCode(query.Area))
        {
            return Result.Failure<PopulationView>(new Error(
                ErrorKind.Validation, "statistics.invalid_area", "An area is a three-digit code, such as 101."));
        }

        // The port's failure passes through unchanged (ADR 0014): NotFound for an unpublished
        // area, Unavailable for the source being down.
        var result = await statistics.GetLatestAsync(query.Area, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Result.Success(new PopulationView(
                result.Value.AreaCode, result.Value.AreaName, result.Value.Period, result.Value.Population, Source))
            : Result.Failure<PopulationView>(result.Error);
    }

    private static bool IsAreaCode([NotNullWhen(true)] string? area) => area is { Length: 3 } && area.All(char.IsAsciiDigit);
}
