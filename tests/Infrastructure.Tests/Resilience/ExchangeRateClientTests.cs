using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Rates;
using AiFramework.Infrastructure.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AiFramework.Infrastructure.Tests.Resilience;

/// <summary>
/// Drives <see cref="ExchangeRateClient"/> through the real resilience pipeline
/// <c>ResilienceRegistration.AddExchangeRateClient</c> attaches, with a
/// <see cref="StubHttpMessageHandler"/> in place of the network and a
/// <see cref="FakeTimeProvider"/> in place of the wall clock. Polly's retry and timeout
/// strategies resolve <see cref="TimeProvider"/> from the DI container the same way any other
/// service is resolved, so registering a fake one here is what lets every backoff and 429
/// Retry-After wait elapse with no real delay.
/// </summary>
/// <remarks>
/// The mechanism is NOT <see cref="FakeTimeProvider.AutoAdvanceAmount"/> — that advances the
/// clock only when something repeatedly READS it in a polling loop, which is not how Polly's
/// delays work: each schedules exactly ONE timer via <c>TimeProvider.CreateTimer</c> and awaits
/// its single callback, so a value set on <c>AutoAdvanceAmount</c> is simply never consulted and
/// the timer never fires. Confirmed by a standalone repro that hung past 30 real seconds with it
/// set. The pattern that actually works, used throughout this file via
/// <see cref="AdvanceUntilCompleteAsync{T}"/>: kick the call off without awaiting it, then
/// repeatedly call <see cref="FakeTimeProvider.Advance(TimeSpan)"/> — which fires any due timer
/// callback SYNCHRONOUSLY, inline — until the task completes.
/// </remarks>
public sealed class ExchangeRateClientTests
{
    // 5s steps, 400 iterations: up to ~33 simulated minutes, comfortably past anything any test
    // below asks for, while costing no real time at all between steps.
    private static readonly TimeSpan AdvanceStep = TimeSpan.FromSeconds(5);
    private const int MaxAdvanceIterations = 400;

    private static (IExchangeRateProvider Provider, StubHttpMessageHandler Stub, FakeTimeProvider Clock) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Action<ResilienceOptions>? configure = null)
    {
        var stub = new StubHttpMessageHandler(respond);
        var clock = new FakeTimeProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddResilience();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddExchangeRateClient();

        // A second AddHttpClient<...>() call for the SAME typed client name layers onto the
        // first rather than replacing it: ConfigurePrimaryHttpMessageHandler swaps only the
        // innermost handler that would otherwise make the real call, and the resilience
        // DelegatingHandlers AddExchangeRateClient already attached stay exactly where they were.
        services.AddHttpClient<IExchangeRateProvider, ExchangeRateClient>()
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IExchangeRateProvider>(), stub, clock);
    }

    /// <summary>
    /// Runs <paramref name="task"/> to completion by advancing <paramref name="clock"/> in fixed
    /// steps rather than awaiting it directly, which would hang forever against a
    /// <see cref="FakeTimeProvider"/> that nothing is advancing. Throws, rather than awaiting an
    /// incomplete task at the end, so a scenario that genuinely never completes fails fast with a
    /// clear message instead of hanging the test run.
    /// </summary>
    private static async Task<T> AdvanceUntilCompleteAsync<T>(FakeTimeProvider clock, Task<T> task)
    {
        // await Task.Yield() between advances, not a tight synchronous loop: a bare console app
        // (used to isolate this) completes with a synchronous loop alone, but under xUnit's test
        // execution context the timer callback's continuation is POSTED rather than run inline,
        // and nothing pumps it back to running until something actually yields - so a
        // synchronous loop advances the clock correctly but the awaited task never observes it,
        // and hangs forever. Task.Yield introduces no real delay of its own; it only gives that
        // context a turn to run what Advance() already queued.
        var iterations = 0;
        while (!task.IsCompleted && iterations < MaxAdvanceIterations)
        {
            clock.Advance(AdvanceStep);
            await Task.Yield();
            iterations++;
        }

        if (!task.IsCompleted)
        {
            throw new TimeoutException(
                $"The operation did not complete after advancing the fake clock " +
                $"{MaxAdvanceIterations} times ({AdvanceStep} each).");
        }

        return await task.ConfigureAwait(false);
    }

    private static HttpResponseMessage SuccessResponse(decimal rate) => new(HttpStatusCode.OK)
    {
        // A plain Replace rather than raw-string interpolation ($$"""..."""): the literal JSON
        // needs four consecutive closing braces right after the substituted value, which C#'s
        // raw-string interpolation cannot disambiguate at $$ without an awkward third $.
        Content = new StringContent(
            """{"amount":1.0,"base":"EUR","date":"2026-09-13","rates":{"USD":RATE}}"""
                .Replace("RATE", rate.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage StatusResponse(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } delta)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
        }

        return response;
    }

    [Fact]
    public async Task GetRateAsync_WhenTheFirstAttemptAnswers503_RetriesAndSucceeds()
    {
        var attempt = 0;
        var (provider, stub, clock) = Build(_ =>
            ++attempt == 1 ? StatusResponse(HttpStatusCode.ServiceUnavailable) : SuccessResponse(1.08m));

        var result = await AdvanceUntilCompleteAsync(
            clock, provider.GetRateAsync("EUR", "USD", CancellationToken.None));

        result.IsSuccess.Should().BeTrue();
        result.Value.Rate.Should().Be(1.08m);
        stub.CallCount.Should().Be(2, "one failed attempt, then one retry that succeeded");
    }

    [Fact]
    public async Task GetRateAsync_WhenEveryAttemptAnswers503_ReturnsUnavailableAfterExhaustingRetries()
    {
        var (provider, stub, clock) = Build(_ => StatusResponse(HttpStatusCode.ServiceUnavailable));

        var result = await AdvanceUntilCompleteAsync(
            clock, provider.GetRateAsync("EUR", "USD", CancellationToken.None));

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
        result.Error.Code.Should().Be("rates.provider_unavailable");

        // ResilienceOptions' default MaxRetryAttempts is 3, so 1 initial attempt + 3 retries.
        stub.CallCount.Should().Be(4);
    }

    [Fact]
    public async Task GetRateAsync_WithABadRequestResponse_DoesNotRetry()
    {
        var (provider, stub, clock) = Build(_ => StatusResponse(HttpStatusCode.BadRequest));

        var result = await AdvanceUntilCompleteAsync(
            clock, provider.GetRateAsync("EUR", "USD", CancellationToken.None));

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);

        // The point of the test: an ordinary 4xx is not in the standard handler's retry
        // predicate, so this call reaches the adapter on the FIRST attempt, not the last of four.
        stub.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetRateAsync_WithANotFoundResponse_DoesNotRetry()
    {
        var (provider, stub, clock) = Build(_ => StatusResponse(HttpStatusCode.NotFound));

        var result = await AdvanceUntilCompleteAsync(
            clock, provider.GetRateAsync("XXX", "USD", CancellationToken.None));

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.NotFound);
        stub.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetRateAsync_OnA429WithRetryAfter_WaitsTheHeaderValueRatherThanTheComputedBackoff()
    {
        // 20s: comfortably past the pipeline's own ~2s default backoff (so honouring the header
        // is distinguishable from ignoring it), and well inside the widened total timeout below.
        var retryAfter = TimeSpan.FromSeconds(20);
        var attempt = 0;
        var (provider, stub, clock) = Build(
            _ => ++attempt == 1
                ? StatusResponse(HttpStatusCode.TooManyRequests, retryAfter)
                : SuccessResponse(1.08m),
            // The default TotalRequestTimeout (30s) sits close enough to a 20s Retry-After that
            // jitter elsewhere in the pipeline could flake the assertion below. Widened only for
            // this test, and only because the SCENARIO calls for a wait long enough to tell
            // "honoured the header" apart from "used the default backoff" - not a claim that
            // real deployments need anything like this budget.
            configure: o => o.TotalRequestTimeout = TimeSpan.FromMinutes(2));

        var before = clock.GetUtcNow();
        var result = await AdvanceUntilCompleteAsync(
            clock, provider.GetRateAsync("EUR", "USD", CancellationToken.None));
        var elapsed = clock.GetUtcNow() - before;

        result.IsSuccess.Should().BeTrue();
        stub.CallCount.Should().Be(2);

        // If the standard handler ignored the header, it would fall back to its own ~2s
        // exponential-plus-jitter backoff instead - nowhere near twenty seconds. This is what
        // distinguishes "the header was honoured" from "a retry merely happened to occur".
        elapsed.Should().BeGreaterThanOrEqualTo(retryAfter,
            "HttpRetryStrategyOptions.ShouldRetryAfterHeader defaults to true, and the provider's " +
            "own Retry-After must win over the pipeline's computed delay");
    }

    [Fact]
    public async Task GetRateAsync_WithResilienceDisabled_CallsExactlyOnceRegardlessOfFailures()
    {
        var (provider, stub, clock) = Build(
            _ => StatusResponse(HttpStatusCode.ServiceUnavailable),
            configure: o => o.Enabled = false);

        var result = await AdvanceUntilCompleteAsync(
            clock, provider.GetRateAsync("EUR", "USD", CancellationToken.None));

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
        stub.CallCount.Should().Be(1,
            "Enabled = false rejects every outcome in the retry predicate - one attempt, no backoff wait");
    }

    [Fact]
    public async Task GetRateAsync_WithASuccessfulResponse_ParsesTheRateAndDate()
    {
        var (provider, _, clock) = Build(_ => SuccessResponse(1.0842m));

        var result = await AdvanceUntilCompleteAsync(
            clock, provider.GetRateAsync("EUR", "USD", CancellationToken.None));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new ExchangeRate("EUR", "USD", 1.0842m, new DateOnly(2026, 9, 13)));
    }

    [Fact]
    public async Task GetRateAsync_WhenTheResponseHasNoRateForTheRequestedQuote_ReturnsUnavailable()
    {
        // A 200 whose body genuinely lacks the requested currency - the provider's contract
        // violated, not this request's fault, so it is treated the same as any other transport
        // failure rather than surfacing as an unhandled KeyNotFoundException.
        var (provider, _, clock) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"amount":1.0,"base":"EUR","date":"2026-09-13","rates":{}}""",
                Encoding.UTF8, "application/json"),
        });

        var result = await AdvanceUntilCompleteAsync(
            clock, provider.GetRateAsync("EUR", "USD", CancellationToken.None));

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }

    [Fact]
    public async Task GetRateAsync_WithANullBaseCurrency_Throws()
    {
        using var httpClient = new HttpClient();
        var client = new ExchangeRateClient(httpClient);

        var act = () => client.GetRateAsync(null!, "USD", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetRateAsync_WithANullQuoteCurrency_Throws()
    {
        using var httpClient = new HttpClient();
        var client = new ExchangeRateClient(httpClient);

        var act = () => client.GetRateAsync("EUR", null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
