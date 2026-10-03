using Npgsql;
using OpenTelemetry.Metrics;

namespace AiFramework.Infrastructure.Observability;

public static class InfrastructureMetrics
{
    /// <summary>
    /// Database metrics, via Npgsql.OpenTelemetry: operation duration and the connection pool's
    /// state (<c>db.client.connection.count</c> by state, against its maximum) — the pool being
    /// the resource a small managed Postgres runs out of first. See ADR 0027.
    /// </summary>
    /// <remarks>
    /// Lives here, not in either host, for the reason <see cref="InfrastructureTracing"/> gives:
    /// only Infrastructure references Npgsql.
    /// </remarks>
    public static MeterProviderBuilder AddInfrastructureMetrics(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddNpgsqlInstrumentation();
    }
}
