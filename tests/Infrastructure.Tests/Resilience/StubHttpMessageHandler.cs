namespace AiFramework.Infrastructure.Tests.Resilience;

/// <summary>
/// Replaces the primary handler at the bottom of a typed client's pipeline — the thing that
/// would otherwise make the real network call — while leaving whatever <c>DelegatingHandler</c>s
/// <c>IHttpClientBuilder</c> attached above it (the resilience handler, in particular) in place.
/// Wired in via <c>ConfigurePrimaryHttpMessageHandler</c>, never resolved from the DI container
/// directly.
/// </summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
{
    private int _callCount;

    public int CallCount => _callCount;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(respond(request));
    }
}
