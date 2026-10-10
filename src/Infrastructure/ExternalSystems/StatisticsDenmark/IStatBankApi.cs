using Refit;

namespace AiFramework.Infrastructure.ExternalSystems.StatisticsDenmark;

/// <summary>
/// Statistics Denmark's StatBank API (https://www.dst.dk/en/Statistik/hjaelp-til-statistikbanken/api),
/// no authentication, relative to the configured base address, which includes <c>/v1/</c>.
/// </summary>
/// <remarks>
/// StatBank takes each function as a POSTed JSON body or as a GET with the same parameters in the
/// URL. These are reads, and they use GET so the repo's rule holds as written — a non-GET call
/// carries an idempotency key or disables retry (ADR 0014) — rather than retrying a POST because
/// it happens to be safe. A variable selection is a query parameter named after the variable;
/// Refit percent-encodes <c>OMRÅDE</c> as UTF-8, which StatBank accepts (checked live, 2026-10-10).
/// </remarks>
internal interface IStatBankApi
{
    [Get("/tableinfo/{table}")]
    public Task<IApiResponse<StatBankTableInfo>> GetTableInfoAsync(
        string table, string format, string lang, CancellationToken cancellationToken);

    [Get("/data/{table}/JSONSTAT")]
    public Task<IApiResponse<StatBankJsonStat>> GetJsonStatAsync(
        string table,
        [AliasAs(StatBankAdapter.AreaVariable)] string area,
        [AliasAs(StatBankAdapter.TimeVariable)] string time,
        string lang,
        CancellationToken cancellationToken);
}
