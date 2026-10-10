namespace AiFramework.Api.Statistics;

/// <summary>An area's latest published population, with the attribution its licence requires.</summary>
public sealed record PopulationResponse
{
    public required string AreaCode { get; init; }

    public required string AreaName { get; init; }

    /// <summary>The quarter the figure is for, as the source labels it (e.g. <c>2026Q3</c>).</summary>
    public required string Period { get; init; }

    public required long Population { get; init; }

    public required string Source { get; init; }
}

public sealed record PopulationAreaResponse
{
    public required string Code { get; init; }

    public required string Name { get; init; }
}

public sealed record PopulationAreasResponse
{
    public required IReadOnlyList<PopulationAreaResponse> Areas { get; init; }

    public required string Source { get; init; }
}
