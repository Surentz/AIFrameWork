using System.Net;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems.Http;
using AiFramework.Infrastructure.Tests.Resilience;
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
    public async Task Send_WhenTheCallerCancels_RecordsNothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => SendAsync(
            new StubHttpMessageHandler(_ => throw new OperationCanceledException(cancelled.Token)),
            cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        _recorder.DidNotReceiveWithAnyArgs().Record(default, default!, default, default);
    }
}
