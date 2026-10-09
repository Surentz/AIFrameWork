using System.Diagnostics.Metrics;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class ExternalSystemMetricsTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _provider;
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> _seen = [];

    public ExternalSystemMetricsTests()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<ExternalSystemMetrics>();
        _provider = services.BuildServiceProvider();

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, ExternalSystemMetrics.MeterName, StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, value, tags, _) => Record(i, value, tags));
        _listener.SetMeasurementEventCallback<double>((i, value, tags, _) => Record(i, value, tags));
        _listener.Start();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _seen.Add((instrument.Name, value, new Dictionary<string, object?>(tags.ToArray(), StringComparer.Ordinal)));

    public void Dispose()
    {
        _listener.Dispose();
        _provider.Dispose();
    }

    [Theory]
    [InlineData(TrafficOutcome.Succeeded, "succeeded")]
    [InlineData(TrafficOutcome.Failed, "failed")]
    [InlineData(TrafficOutcome.Faulted, "faulted")]
    public void RecordCall_CountsOneCallTaggedWithSystemAndOutcome(TrafficOutcome outcome, string expected)
    {
        var metrics = _provider.GetRequiredService<ExternalSystemMetrics>();

        metrics.RecordCall("Partner", outcome);

        var call = _seen.Should().ContainSingle(s => s.Instrument == "aiframework.external_system.calls").Subject;
        call.Value.Should().Be(1);
        call.Tags["system"].Should().Be("Partner");
        call.Tags["outcome"].Should().Be(expected);
    }

    [Fact]
    public void CertificateGauge_ReportsSecondsUntilNotAfter()
    {
        var metrics = _provider.GetRequiredService<ExternalSystemMetrics>();
        metrics.SetCertificateNotAfter("Partner", _clock.GetUtcNow().AddDays(10));

        _listener.RecordObservableInstruments();

        var gauge = _seen.Should().ContainSingle(s => s.Instrument == "aiframework.external_system.certificate.time_remaining").Subject;
        gauge.Value.Should().BeApproximately(TimeSpan.FromDays(10).TotalSeconds, 1);
        gauge.Tags["system"].Should().Be("Partner");
    }

    [Fact]
    public void CertificateGauge_ForASystemWhoseCertificateWasCleared_ReportsNothing()
    {
        var metrics = _provider.GetRequiredService<ExternalSystemMetrics>();
        metrics.SetCertificateNotAfter("Partner", _clock.GetUtcNow().AddDays(10));
        metrics.SetCertificateNotAfter("Partner", notAfter: null);

        _listener.RecordObservableInstruments();

        _seen.Should().NotContain(s => s.Instrument == "aiframework.external_system.certificate.time_remaining");
    }
}
