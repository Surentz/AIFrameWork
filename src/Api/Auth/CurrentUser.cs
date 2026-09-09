using System.Security.Claims;
using AiFramework.Application.Abstractions;

namespace AiFramework.Api.Auth;

/// <summary>
/// The caller, read from the cookie's claims. The only place NameIdentifier is parsed —
/// AuthController used to do it privately, and a second parser is a second thing to get wrong.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    // Memoizes the first NON-NULL id, not the first read: a read before sign-in legitimately
    // finds no user and must keep retrying, so nothing that reads Id pre-auth changes behaviour.
    // What this guards against is different — a LIVE read of HttpContext is only valid on the
    // request's own ambient execution context, and the caching behavior's cache-miss factory
    // (Infrastructure/Messaging/Behaviors.cs, CachedAsync) runs the query handler off that
    // context by design, for HybridCache's concurrent-caller ("stampede") protection: a shared
    // computation must not depend on any one caller's ambient state. Without this memo, a
    // handler running inside that factory sees a null HttpContext and reports the caller
    // unauthenticated, even though CachedAsync read this same scoped instance's Id successfully
    // moments earlier, before ever entering the factory. This class is scoped — one instance per
    // request — so caching the first non-null value here cannot leak across requests or users.
    //
    // This also relies on cache keys being user-scoped: a joined stampede caller is served a
    // value computed under the originating caller's request scope, which is safe only because
    // every such caller is, by construction, the same user.
    private Guid? _id;

    public Guid? Id =>
        _id ??= Guid.TryParse(
            accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;
}
