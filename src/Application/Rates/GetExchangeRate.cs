using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Rates;

public sealed record ExchangeRateView(string From, string To, decimal Rate, DateOnly AsOf);

public sealed record GetExchangeRate(string From, string To) : IQuery<ExchangeRateView>, ICacheable
{
    // Upper-cased here too, not only in the handler: two requests differing only by case ("eur"
    // vs "EUR") name the same rate, and a CacheKey that did not normalize would cache them
    // separately for no reason. CacheKey is the query's OWN arguments only — the caching
    // behavior prepends the query type name and the caller's id, per Application/CLAUDE.md.
    public string CacheKey => $"{From.ToUpperInvariant()}:{To.ToUpperInvariant()}";

    // One minute, longer than GetOrders/GetProducts' thirty seconds: a foreign-exchange rate
    // does not move meaningfully inside either window, and the point of caching THIS query is
    // as much about not spending a third party's own rate limit on every repeat request as it
    // is about latency. ADR 0014.
    public TimeSpan Duration => TimeSpan.FromMinutes(1);
}

/// <summary>
/// Validates its own inputs, because QueryDispatcher runs no validation behavior for queries —
/// that is command-only (Infrastructure/CLAUDE.md). A malformed currency code is a 400, and
/// never becomes a wasted call to the provider or its resilience pipeline.
/// </summary>
public sealed class GetExchangeRateHandler(IExchangeRateProvider provider)
    : IQueryHandler<GetExchangeRate, ExchangeRateView>
{
    public async Task<Result<ExchangeRateView>> HandleAsync(
        GetExchangeRate query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!IsIsoCurrencyCode(query.From))
        {
            return Result.Failure<ExchangeRateView>(new Error(
                ErrorKind.Validation, "rates.invalid_currency_code",
                $"'{query.From}' is not a three-letter currency code."));
        }

        if (!IsIsoCurrencyCode(query.To))
        {
            return Result.Failure<ExchangeRateView>(new Error(
                ErrorKind.Validation, "rates.invalid_currency_code",
                $"'{query.To}' is not a three-letter currency code."));
        }

        var from = query.From.ToUpperInvariant();
        var to = query.To.ToUpperInvariant();

        // The port's own failure passes through unchanged: Unavailable when its resilience
        // pipeline exhausted its retry budget, NotFound/Validation for a pair the provider
        // itself rejects. This handler does not retry, translate, or catch anything — ADR 0014
        // puts exactly one retry layer on this call path, and it is inside the adapter behind
        // IExchangeRateProvider, never here.
        var result = await provider.GetRateAsync(from, to, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Result.Success(new ExchangeRateView(from, to, result.Value.Rate, result.Value.AsOf))
            : Result.Failure<ExchangeRateView>(result.Error);
    }

    private static bool IsIsoCurrencyCode(string? value) =>
        value is { Length: 3 } && value.All(char.IsAsciiLetter);
}
