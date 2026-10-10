using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// The external systems' OpenTelemetry instruments, for the two alert rules (ADR 0032): a call
/// counter by system and outcome, and seconds until each client certificate expires. Prometheus
/// sees <c>aiframework_external_system_calls_total</c> and
/// <c>aiframework_external_system_certificate_time_remaining_seconds</c>.
/// </summary>
public sealed class ExternalSystemMetrics
{
    public const string MeterName = "AiFramework.ExternalSystems";

    private readonly Counter<long> _calls;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _certificateNotAfter =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    public ExternalSystemMetrics(IMeterFactory meters, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _time = time;

        // The factory owns the meter and disposes it with the container; disposing it here would unpublish it.
#pragma warning disable CA2000
        var meter = meters.Create(MeterName);
#pragma warning restore CA2000
        _calls = meter.CreateCounter<long>(
            "aiframework.external_system.calls", unit: "{call}",
            description: "Calls to an external system, counted outside retry, by outcome.");
        meter.CreateObservableGauge(
            "aiframework.external_system.certificate.time_remaining", ObserveCertificates, unit: "s",
            description: "Seconds until an external system's client certificate expires.");
    }

    internal void RecordCall(string system, TrafficOutcome outcome) =>
        _calls.Add(
            1,
            new KeyValuePair<string, object?>("system", system),
            new KeyValuePair<string, object?>("outcome", outcome switch
            {
                TrafficOutcome.Succeeded => "succeeded",
                TrafficOutcome.Failed => "failed",
                _ => "faulted",
            }));

    /// <summary>Set by the worker's status publisher each run; null removes the system's series.</summary>
    internal void SetCertificateNotAfter(string system, DateTimeOffset? notAfter)
    {
        if (notAfter is { } value)
        {
            _certificateNotAfter[system] = value;
        }
        else
        {
            _certificateNotAfter.TryRemove(system, out _);
        }
    }

    private IEnumerable<Measurement<double>> ObserveCertificates()
    {
        var now = _time.GetUtcNow();
        return _certificateNotAfter.Select(pair => new Measurement<double>(
            (pair.Value - now).TotalSeconds, new KeyValuePair<string, object?>("system", pair.Key)));
    }
}
