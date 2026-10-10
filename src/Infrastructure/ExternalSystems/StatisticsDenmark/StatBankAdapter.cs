using System.Net;
using System.Text.Json;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Statistics;
using Microsoft.Extensions.Logging;

namespace AiFramework.Infrastructure.ExternalSystems.StatisticsDenmark;

/// <summary>
/// <see cref="IPopulationStatistics"/> from StatBank's FOLK1A table (population on the first day
/// of the quarter). The external systems pilot (ADR 0031): no certificate and no token, so it
/// exercises the client, <see cref="ExternalSystemCall"/>, configuration and monitoring alone.
/// </summary>
/// <remarks>
/// <para>
/// StatBank answers every error with a 400 and says what went wrong in the body's
/// <c>errorTypeCode</c>; <c>EXTRACT-NOTFOUND</c> is an unknown table, variable or value. The
/// table and the variables are this adapter's constants, pinned by its tests, so here it can only
/// mean an area code StatBank does not publish. Every other error type is our request being
/// wrong, which is Unavailable like any answer outside the contract.
/// </para>
/// <para>
/// The trade-off: if StatBank renamed FOLK1A, <c>OMRÅDE</c> or <c>Tid</c>, every lookup would read
/// as a 404 with no Warning, because the body that would say so is never logged. The area list
/// is the canary: it reads the same table's variables and logs "FOLK1A has no area variable" at
/// Warning, and the page goes Unavailable.
/// </para>
/// </remarks>
internal sealed class StatBankAdapter(IStatBankApi api, ILogger<StatBankAdapter> logger) : IPopulationStatistics
{
    public const string SystemName = "StatisticsDenmark";

    public const string AreaVariable = "OMRÅDE";

    public const string TimeVariable = "Tid";

    private const string PopulationTable = "FOLK1A";

    private const string Language = "en";

    /// <summary>StatBank's selector for the most recent period of the time variable.</summary>
    private const string LatestPeriod = "(1)";

    public Task<Result<AreaPopulation>> GetLatestAsync(string areaCode, CancellationToken cancellationToken) =>
        ExternalSystemCall.SendAsync(
            SystemName,
            logger,
            token => api.GetJsonStatAsync(PopulationTable, areaCode, LatestPeriod, Language, token),
            body => ToPopulation(areaCode, body.Dataset),
            rejection => IsNotFound(rejection)
                ? new Error(ErrorKind.NotFound, "statistics.area_not_found", $"No population figures are published for area '{areaCode}'.")
                : null,
            cancellationToken);

    public Task<Result<IReadOnlyList<StatisticsArea>>> GetAreasAsync(CancellationToken cancellationToken) =>
        ExternalSystemCall.SendAsync(
            SystemName,
            logger,
            token => api.GetTableInfoAsync(PopulationTable, "JSON", Language, token),
            ToAreas,
            expected: null,
            cancellationToken);

    private Result<AreaPopulation> ToPopulation(string areaCode, StatBankDataset dataset)
    {
        // One area and one period selected, every other variable eliminated to its total: exactly
        // one cell, labelled with the area asked for. Anything else is StatBank answering a
        // different question. The reasons are this adapter's own text, never StatBank's.
        if (dataset.Value.Count != 1 || dataset.Dimension.Time.Category.Label.Count != 1)
        {
            return ExternalSystemCall.NotItsContract<AreaPopulation>(SystemName, logger, "FOLK1A did not answer with exactly one cell");
        }

        if (!dataset.Dimension.Area.Category.Label.TryGetValue(areaCode, out var areaName))
        {
            return ExternalSystemCall.NotItsContract<AreaPopulation>(SystemName, logger, "FOLK1A's cell is not labelled with the area asked for");
        }

        if (dataset.Value[0] is not { } population)
        {
            return Result.Failure<AreaPopulation>(new Error(
                ErrorKind.NotFound, "statistics.population_not_published", $"No population figure is published for area '{areaCode}'."));
        }

        return Result.Success(new AreaPopulation(areaCode, areaName, dataset.Dimension.Time.Category.Label.Values.Single(), population));
    }

    private Result<IReadOnlyList<StatisticsArea>> ToAreas(StatBankTableInfo info)
    {
        var area = info.Variables.FirstOrDefault(variable => string.Equals(variable.Id, AreaVariable, StringComparison.Ordinal));
        return area is null
            ? ExternalSystemCall.NotItsContract<IReadOnlyList<StatisticsArea>>(SystemName, logger, "FOLK1A has no area variable")
            : Result.Success<IReadOnlyList<StatisticsArea>>([.. area.Values.Select(value => new StatisticsArea(value.Id, value.Text))]);
    }

    private static bool IsNotFound(ExternalSystemRejection rejection) =>
        rejection.Status == HttpStatusCode.BadRequest
        && string.Equals(ErrorTypeCode(rejection.Content), "EXTRACT-NOTFOUND", StringComparison.Ordinal);

    /// <summary>StatBank's <c>errorTypeCode</c>, or null for a body that is not its error shape.</summary>
    private static string? ErrorTypeCode(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("errorTypeCode", out var code)
                   && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
