using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Rates;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace AiFramework.Infrastructure.Resilience;

/// <summary>
/// <see cref="IExchangeRateProvider"/> against api.frankfurter.dev — ADR 0014's reference
/// integration. Everything Polly touches lives behind this one class: <see cref="ResilienceRegistration"/>
/// attaches the pipeline to the <see cref="HttpClient"/> this constructor receives, and this
/// class converts only the FINAL outcome — after every retry, the circuit breaker, and both
/// timeouts have had their say — into a <see cref="Result{T}"/>.
/// </summary>
/// <remarks>
/// Never return a failed <see cref="Result{T}"/> from a call this class makes to
/// <c>httpClient</c> before that call has fully resolved. Polly decides whether to retry by
/// inspecting an exception or the <see cref="HttpResponseMessage"/> itself — a <c>Result</c> is
/// invisible to it. A <c>Result</c> constructed too early, inside a loop or a helper Polly's
/// handler still wraps, would read as an ordinary return value and silently disable every retry.
/// ADR 0014.
/// </remarks>
public sealed class ExchangeRateClient(HttpClient httpClient) : IExchangeRateProvider
{
    public async Task<Result<ExchangeRate>> GetRateAsync(
        string baseCurrency, string quoteCurrency, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseCurrency);
        ArgumentNullException.ThrowIfNull(quoteCurrency);

        HttpResponseMessage response;
        try
        {
            // Relative to HttpClient.BaseAddress, which ResilienceRegistration normalizes to
            // always end in '/' - Uri's own combining rule otherwise drops BaseAddress's last
            // path segment ("v1") as though it were a file name, silently requesting the wrong
            // path. Codes are already validated as three ASCII letters by GetExchangeRateHandler
            // before this is ever called, so no character here needs escaping - Uri.EscapeDataString
            // is used anyway, as the correct default rather than an exception to it.
            var requestUri =
                $"latest?from={Uri.EscapeDataString(baseCurrency)}&to={Uri.EscapeDataString(quoteCurrency)}";

            response = await httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        }
        // These three are what escapes the pipeline once it has genuinely given up: a retry
        // budget exhausted on a connection failure, the total or per-attempt timeout firing, or
        // the circuit breaker open and failing fast without attempting the call at all. All three
        // are the transport's own failure, not this request's - Unavailable, never a throw.
        // OperationCanceledException is deliberately NOT caught here: TimeoutRejectedException is
        // how POLLY'S OWN timeout surfaces, but a plain cancellation is the CALLER's, via
        // cancellationToken, and must propagate rather than become a Result.
        catch (HttpRequestException exception)
        {
            return Unavailable(exception.Message, retryAfter: null);
        }
        catch (TimeoutRejectedException exception)
        {
            return Unavailable(exception.Message, retryAfter: null);
        }
        catch (BrokenCircuitException exception)
        {
            return Unavailable(exception.Message, retryAfter: null);
        }

        using (response)
        {
            return response.IsSuccessStatusCode
                ? await ParseAsync(baseCurrency, quoteCurrency, response, cancellationToken)
                    .ConfigureAwait(false)
                : MapFailureStatus(baseCurrency, quoteCurrency, response);
        }
    }

    /// <summary>
    /// 400 and 404 are the provider rejecting the PAIR, not a transient failure, and the
    /// standard resilience handler does not retry either - so reaching this method with one of
    /// those means the FIRST attempt already came back this way. Every other non-success status
    /// means retries ran and the pipeline still gave up (5xx, or a 408/429 that outlasted its own
    /// budget) - the transport's failure, same shape as GetRateAsync's three caught exceptions.
    /// </summary>
    private static Result<ExchangeRate> MapFailureStatus(
        string baseCurrency, string quoteCurrency, HttpResponseMessage response) =>
        response.StatusCode switch
        {
            HttpStatusCode.BadRequest => Result.Failure<ExchangeRate>(new Error(
                ErrorKind.Validation, "rates.provider_rejected_pair",
                $"The provider rejected '{baseCurrency}/{quoteCurrency}' as malformed.")),
            HttpStatusCode.NotFound => Result.Failure<ExchangeRate>(new Error(
                ErrorKind.NotFound, "rates.pair_not_found",
                $"No rate is published for '{baseCurrency}/{quoteCurrency}'.")),
            _ => Unavailable(
                $"The provider answered {(int)response.StatusCode}.",
                ResponseRetryAfter(response)),
        };

    private static async Task<Result<ExchangeRate>> ParseAsync(
        string baseCurrency, string quoteCurrency, HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        FrankfurterResponse? body;
        try
        {
            body = await response.Content
                .ReadFromJsonAsync<FrankfurterResponse>(cancellationToken)
                .ConfigureAwait(false);
        }
        // A 200 with a body the provider's own contract does not describe is the provider's
        // fault, not this request's - Unavailable rather than a 500, and rather than letting
        // JsonException escape as an unhandled exception with no ErrorKind at all.
        catch (System.Text.Json.JsonException exception)
        {
            return Unavailable($"The provider's response could not be parsed: {exception.Message}", null);
        }

        if (body is null || !body.Rates.TryGetValue(quoteCurrency, out var rate))
        {
            return Unavailable(
                $"The provider's response for '{baseCurrency}/{quoteCurrency}' had no rate.", null);
        }

        return Result.Success(new ExchangeRate(baseCurrency, quoteCurrency, rate, body.Date));
    }

    /// <summary>
    /// The provider's OWN Retry-After, when the final failed response happened to carry one -
    /// a producer with a better number than ResultExtensions.Problem's fixed floor, per the
    /// mechanism Task 2 built for exactly this. A bare seconds value and an HTTP-date are both
    /// legal per RFC 9110; <see cref="System.Net.Http.Headers.RetryConditionHeaderValue"/>
    /// parses either and exposes whichever the server sent as ONE of Delta or Date, never both.
    /// </summary>
    private static TimeSpan? ResponseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        if (header.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }

        return null;
    }

    /// <summary>
    /// The one place every transport failure funnels through, whether from a caught exception or
    /// a non-success status: always the same ErrorKind and code, so a caller can branch on those
    /// alone; detail and RetryAfter carry whatever this call site knows, with RetryAfter left
    /// null wherever the provider gave no better number than ResultExtensions.Problem's own floor.
    /// </summary>
    private static Result<ExchangeRate> Unavailable(string detail, TimeSpan? retryAfter) =>
        Result.Failure<ExchangeRate>(new Error(
            ErrorKind.Unavailable, "rates.provider_unavailable", detail, RetryAfter: retryAfter));

    /// <summary>The provider's own shape: <c>{"amount":1.0,"base":"EUR","date":"...","rates":{"USD":1.08}}</c>.</summary>
    private sealed record FrankfurterResponse(
        [property: JsonPropertyName("base")] string Base,
        [property: JsonPropertyName("date")] DateOnly Date,
        [property: JsonPropertyName("rates")] Dictionary<string, decimal> Rates);
}
