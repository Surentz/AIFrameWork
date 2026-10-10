using System.Diagnostics.Metrics;
using System.Net;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.ExternalSystems.Http;
using AiFramework.Infrastructure.Tests.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class OutboundTrafficHandlerTests
{
    private readonly ITrafficRecorder _recorder = Substitute.For<ITrafficRecorder>();

    private async Task SendAsync(HttpMessageHandler inner, CancellationToken cancellationToken = default)
    {
        using var handler = new OutboundTrafficHandler(_recorder, TimeProvider.System, "Sim", TrafficKind.Outbound)
        {
            InnerHandler = inner,
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://partner.example/echo");
        using var response = await invoker.SendAsync(request, cancellationToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, TrafficOutcome.Succeeded)]
    [InlineData(HttpStatusCode.NotFound, TrafficOutcome.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TrafficOutcome.Faulted)]
    public async Task Send_RecordsTheOutcomeOfTheStatus(HttpStatusCode status, TrafficOutcome expected)
    {
        await SendAsync(new StubHttpMessageHandler(_ => new HttpResponseMessage(status)));

        _recorder.Received(1).Record(TrafficKind.Outbound, "Sim", expected, Arg.Any<long>());
    }

    [Fact]
    public async Task Send_WhenTheCallThrows_RecordsAFaultAndRethrows()
    {
        var act = () => SendAsync(new StubHttpMessageHandler(_ => throw new HttpRequestException("refused")));

        await Assert.ThrowsAsync<HttpRequestException>(act);
        _recorder.Received(1).Record(TrafficKind.Outbound, "Sim", TrafficOutcome.Faulted, Arg.Any<long>());
    }

    [Fact]
    public async Task Send_WhenTheCallTimesOutWithoutCallerCancellation_RecordsAFault()
    {
        var act = () => SendAsync(new StubHttpMessageHandler(_ => throw new TaskCanceledException("timed out")));

        await Assert.ThrowsAsync<TaskCanceledException>(act);
        _recorder.Received(1).Record(TrafficKind.Outbound, "Sim", TrafficOutcome.Faulted, Arg.Any<long>());
    }

    [Fact]
    public async Task Send_WhenTheCallerCancels_RecordsNothing()
    {
        using var cancelling = new CancellationTokenSource();
        using var stub = new StubHttpMessageHandler(_ =>
        {
            cancelling.Cancel();
            throw new OperationCanceledException(cancelling.Token);
        });

        var act = () => SendAsync(stub, cancelling.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        stub.CallCount.Should().Be(1);
        _recorder.DidNotReceiveWithAnyArgs().Record(default, default!, default, default);
    }

    [Fact]
    public async Task Send_AsTheOutboundCounter_RecordsTheCallMetric()
    {
        var calls = await SendWithMetricsAsync(TrafficKind.Outbound);

        calls.Should().ContainSingle().Which.Should().Be(("Sim", "faulted"));
    }

    [Fact]
    public async Task Send_AsTheAttemptCounter_RecordsNoCallMetric()
    {
        var calls = await SendWithMetricsAsync(TrafficKind.OutboundAttempt);

        calls.Should().BeEmpty();
    }

    private static async Task<List<(string System, string Outcome)>> SendWithMetricsAsync(TrafficKind kind)
    {
        var calls = new List<(string System, string Outcome)>();
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var metrics = new ExternalSystemMetrics(
            services.GetRequiredService<IMeterFactory>(), TimeProvider.System);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, ExternalSystemMetrics.MeterName, StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var tagged = tags.ToArray().ToDictionary(t => t.Key, t => (string?)t.Value, StringComparer.Ordinal);
            calls.Add((tagged["system"]!, tagged["outcome"]!)); // the counter always sets both.
        });
        listener.Start();

        using var handler = new OutboundTrafficHandler(
            Substitute.For<ITrafficRecorder>(), TimeProvider.System, "Sim", kind, metrics)
        {
            InnerHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://partner.example/echo");
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        return calls;
    }
}
