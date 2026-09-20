using AiFramework.Application.Abstractions;
using Microsoft.Net.Http.Headers;

namespace AiFramework.Api.Auth;

/// <summary>
/// Where the current request came from, read off <c>HttpContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// Registered in the API's own Program.cs, never in <c>AddInfrastructure</c> — the same rule
/// <see cref="ICurrentUser"/> follows, and for the same reason: the last registration wins, so
/// one made inside a shared registration method silently replaces the host's. The worker
/// registers no implementation at all, because it never authenticates anyone.
/// </para>
/// <para>
/// <b><c>RemoteIpAddress</c> is only the caller's address if the hop in front is trusted.</b>
/// Behind the ingress that means <c>ForwardedHeaders__Enabled</c> must be on, which the
/// Kubernetes overlay sets and a plain <c>dotnet run</c> does not need. Without it every address
/// recorded in the cluster is the ingress pod's — a thing that looks like working audit data
/// right up until someone tries to use it. This type deliberately reads the resolved
/// <c>RemoteIpAddress</c> rather than the raw header, so that whatever the forwarded-headers
/// middleware decided is what gets recorded, rather than a second opinion.
/// </para>
/// </remarks>
public sealed class ClientContext(IHttpContextAccessor accessor) : IClientContext
{
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => accessor.HttpContext?.Request.Headers[HeaderNames.UserAgent]
        .ToString() is { Length: > 0 } agent
        ? agent
        : null;
}
