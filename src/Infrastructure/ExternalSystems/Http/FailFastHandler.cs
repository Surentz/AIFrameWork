namespace AiFramework.Infrastructure.ExternalSystems.Http;

/// <summary>
/// The primary handler for a system that cannot be called — no usable certificate, no trust
/// bundle, not configured. Throws the same exception a failed handshake would, so it reaches the
/// adapter as a send that got no response, which ExternalSystemCall maps to Unavailable, and it
/// never touches the network.
/// </summary>
/// <remarks>
/// IHttpClientFactory caches a primary handler for its lifetime (two minutes), so a fixed
/// certificate is picked up on the next rebuild without a restart.
/// </remarks>
internal sealed class FailFastHandler(string systemName, string problem) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            $"External system '{systemName}' cannot be called: {problem}."));
}
