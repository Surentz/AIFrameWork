using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiFramework.Infrastructure.Integration;

/// <summary>
/// The one JSON shape for everything on the broker that another system reads or writes: web
/// defaults (camelCase out, case-insensitive in, unknown members ignored) and enums as names -
/// the same shape the HTTP API already uses.
/// </summary>
/// <remarks>
/// <see cref="Options"/> is read-only, so it is safe to share, but a Wolverine
/// <c>SystemTextJsonSerializer</c> mutates the instance it is given (it adds its own converter),
/// which throws on a read-only instance. Every Wolverine serializer must be built from
/// <c>new JsonSerializerOptions(IntegrationJson.Options)</c> - a private, writable copy - never
/// from <see cref="Options"/> directly.
/// </remarks>
public static class IntegrationJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        // MakeReadOnly() with no TypeInfoResolver set throws InvalidOperationException; the
        // reflection-based resolver these Web defaults imply has to be populated explicitly
        // before the instance can be locked. See the plan's Verified API row V2b.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
