using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Rates;

/// <summary>A point-in-time conversion rate, as reported by the provider.</summary>
public sealed record ExchangeRate(string BaseCurrency, string QuoteCurrency, decimal Rate, DateOnly AsOf);

/// <summary>
/// The one external dependency ADR 0014's reference integration exists to prove a pattern for:
/// a live foreign-exchange rate from a third party. No <c>HttpClient</c>, no Polly type, and no
/// EF type may appear in this file — the dependency-rule hook blocks the first and third
/// outright, and the second is the whole point of the port: this layer describes what it needs,
/// and Infrastructure owns how the call is made, how it retries, and how its failures become a
/// <see cref="Result{T}"/>.
/// </summary>
public interface IExchangeRateProvider
{
    /// <summary>
    /// The current rate to convert one unit of <paramref name="baseCurrency"/> into
    /// <paramref name="quoteCurrency"/>. Both are three-letter, upper-cased ISO 4217 codes —
    /// this port validates neither, because <c>GetExchangeRateHandler</c> already did before
    /// calling it. A failure here is the transport's own — an exhausted retry budget maps to
    /// <see cref="ErrorKind.Unavailable"/>, a pair the provider rejects to
    /// <see cref="ErrorKind.NotFound"/> or <see cref="ErrorKind.Validation"/> as appropriate —
    /// never a thrown exception for an expected failure.
    /// </summary>
    public Task<Result<ExchangeRate>> GetRateAsync(
        string baseCurrency, string quoteCurrency, CancellationToken cancellationToken);
}
