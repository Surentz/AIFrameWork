using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.Security;

/// <summary>
/// <see cref="IClientContext"/> for a host that has no HTTP request to read one from.
/// </summary>
/// <remarks>
/// <para>
/// The worker registers this. It never authenticates anyone, so it would never legitimately
/// resolve <c>ISignInAudit</c> — but the generic host validates EVERY registered descriptor when
/// it builds its container, so "never resolved in practice" is not good enough: without an
/// implementation, `codegen write` on the worker dies with "Unable to resolve service for type
/// IClientContext", and it does so at container-build time, nowhere near the sign-in path. Found
/// exactly that way.
/// </para>
/// <para>
/// Registered by each host rather than by <c>AddInfrastructure</c>, exactly as
/// <see cref="ICurrentUser"/> is: who the caller is, and where they came from, are host-level
/// decisions, and a shared registration would silently replace the API's real one.
/// </para>
/// </remarks>
public sealed class NoClientContext : IClientContext
{
    public string? IpAddress => null;

    public string? UserAgent => null;
}
