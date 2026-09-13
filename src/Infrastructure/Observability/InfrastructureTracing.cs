using Npgsql;
using OpenTelemetry.Trace;

namespace AiFramework.Infrastructure.Observability;

public static class InfrastructureTracing
{
    /// <summary>
    /// Database spans, via Npgsql.OpenTelemetry. A TracerProviderBuilder extension rather than
    /// an IServiceCollection one, matching the shape OpenTelemetry's own instrumentation
    /// packages use (AddAspNetCoreInstrumentation, AddHttpClientInstrumentation).
    /// </summary>
    /// <remarks>
    /// Lives here, not in Api, for the same reason AddInfrastructure is the single entry point
    /// Api reaches Infrastructure through: AddNpgsql() is only callable where the Npgsql package
    /// is referenced, and putting it in Api would mean Api carrying an Npgsql package reference
    /// of its own — reaching past composition into a storage concern, which src/Api/CLAUDE.md
    /// forbids for anything but DI registration.
    /// </remarks>
    public static TracerProviderBuilder AddInfrastructureTracing(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddNpgsql();
    }
}
