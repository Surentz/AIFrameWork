using System.Text.Json.Serialization;

namespace AiFramework.Infrastructure.ExternalSystems.StatisticsDenmark;

// StatBank's own shapes, only the fields the adapter reads. Strict JSON (ExternalSystemsBuilder)
// makes every non-nullable one a promise: if StatBank stops sending it, the call is Unavailable
// and logged as a contract break rather than a record holding a null.

internal sealed record StatBankTableInfo(
    [property: JsonPropertyName("variables")] IReadOnlyList<StatBankVariable> Variables);

internal sealed record StatBankVariable(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("values")] IReadOnlyList<StatBankValue> Values);

internal sealed record StatBankValue(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// StatBank's <c>JSONSTAT</c> format: the JSON-stat 1.x bundle form, one dataset wrapped in
/// <c>dataset</c>, with <c>id</c>, <c>size</c> and <c>role</c> inside <c>dimension</c>.
/// </summary>
internal sealed record StatBankJsonStat(
    [property: JsonPropertyName("dataset")] StatBankDataset Dataset);

/// <summary><c>value</c> is the flattened cube; null where StatBank suppresses a figure.</summary>
internal sealed record StatBankDataset(
    [property: JsonPropertyName("dimension")] StatBankDimensions Dimension,
    [property: JsonPropertyName("value")] IReadOnlyList<long?> Value);

/// <summary>
/// The two dimensions selected. The <c>dimension</c> object also holds <c>id</c>, <c>size</c> and
/// <c>role</c> beside the dimensions, which is why this is not a dictionary.
/// </summary>
internal sealed record StatBankDimensions(
    [property: JsonPropertyName(StatBankAdapter.AreaVariable)] StatBankDimension Area,
    [property: JsonPropertyName(StatBankAdapter.TimeVariable)] StatBankDimension Time);

internal sealed record StatBankDimension(
    [property: JsonPropertyName("category")] StatBankCategory Category);

internal sealed record StatBankCategory(
    [property: JsonPropertyName("label")] IReadOnlyDictionary<string, string> Label);
