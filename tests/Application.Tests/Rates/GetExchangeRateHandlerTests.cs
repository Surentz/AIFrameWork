using AiFramework.Application.Abstractions;
using AiFramework.Application.Rates;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Rates;

public sealed class GetExchangeRateHandlerTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 13);

    private readonly IExchangeRateProvider _provider = Substitute.For<IExchangeRateProvider>();

    [Fact]
    public async Task HandleAsync_WithAValidPair_ReturnsTheView()
    {
        _provider.GetRateAsync("EUR", "USD", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ExchangeRate("EUR", "USD", 1.08m, AsOf)));

        var result = await new GetExchangeRateHandler(_provider)
            .HandleAsync(new GetExchangeRate("EUR", "USD"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new ExchangeRateView("EUR", "USD", 1.08m, AsOf));
    }

    [Fact]
    public async Task HandleAsync_WithLowercaseCodes_UppercasesBeforeCallingTheProvider()
    {
        _provider.GetRateAsync("EUR", "USD", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ExchangeRate("EUR", "USD", 1.08m, AsOf)));

        var result = await new GetExchangeRateHandler(_provider)
            .HandleAsync(new GetExchangeRate("eur", "usd"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.From.Should().Be("EUR");
        result.Value.To.Should().Be("USD");
        await _provider.Received(1).GetRateAsync("EUR", "USD", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", "USD")]
    [InlineData("EU", "USD")]
    [InlineData("EURO", "USD")]
    [InlineData("EU1", "USD")]
    [InlineData("USD", "")]
    [InlineData("USD", "US")]
    public async Task HandleAsync_WithAMalformedCurrencyCode_ReturnsValidationWithoutCallingTheProvider(
        string from, string to)
    {
        var result = await new GetExchangeRateHandler(_provider)
            .HandleAsync(new GetExchangeRate(from, to), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("rates.invalid_currency_code");

        // A wasted call to the provider is a wasted call into its resilience pipeline, and
        // eventually a wasted request against the third party's own rate limit - ADR 0014.
        await _provider.DidNotReceive().GetRateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenTheProviderReportsUnavailable_PassesTheFailureThroughUnchanged()
    {
        var unavailable = new Error(
            ErrorKind.Unavailable, "rates.provider_unavailable", "The provider did not respond.",
            RetryAfter: TimeSpan.FromSeconds(10));
        _provider.GetRateAsync("EUR", "USD", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ExchangeRate>(unavailable));

        var result = await new GetExchangeRateHandler(_provider)
            .HandleAsync(new GetExchangeRate("EUR", "USD"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(unavailable);
    }

    [Fact]
    public Task HandleAsync_WithNullQuery_Throws()
    {
        var act = () => new GetExchangeRateHandler(_provider)
            .HandleAsync(null!, CancellationToken.None);

        return act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void CacheKey_UppercasesBothCodes()
    {
        // No user id: the caching behavior prepends the query type and the caller, and adding
        // one here would duplicate it rather than secure anything.
        new GetExchangeRate("eur", "usd").CacheKey.Should().Be("EUR:USD");
    }

    [Fact]
    public void CacheKey_ForDifferingCaseInputs_IsTheSame()
    {
        new GetExchangeRate("eur", "USD").CacheKey.Should().Be(
            new GetExchangeRate("EUR", "usd").CacheKey);
    }

    [Fact]
    public void Duration_IsOneMinute()
    {
        new GetExchangeRate("EUR", "USD").Duration.Should().Be(TimeSpan.FromMinutes(1));
    }
}
