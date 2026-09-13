namespace AiFramework.Api.Rates;

public sealed record ExchangeRateResponse
{
    public required string From { get; init; }

    public required string To { get; init; }

    public required decimal Rate { get; init; }

    public required DateOnly AsOf { get; init; }
}
