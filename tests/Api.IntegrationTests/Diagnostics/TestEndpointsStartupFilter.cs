using AiFramework.Api;
using AiFramework.Application.Abstractions;
using AiFramework.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;

namespace AiFramework.Api.IntegrationTests.Diagnostics;

/// <summary>
/// Adds paths that produce a known outcome on demand, so failure mappings that no real feature
/// happens to exercise can still be proven over real HTTP. Two throw on demand, exercising
/// <c>GlobalExceptionHandler</c>'s two branches (DomainException -> 400, anything else -> 500
/// with no leaked detail); a third returns a failed <see cref="Result{T}"/> carrying
/// <see cref="ErrorKind.Unavailable"/>, exercising <c>ResultExtensions.Problem</c>'s 503 branch
/// ahead of any handler that produces one for real (ADR 0014's exchange-rate client is the
/// first). Registered by <c>ApiFactory</c> as an <see cref="IStartupFilter"/> - the supported
/// way to add test-only middleware to a minimal-hosting-model app (<c>WebApplication</c>) from a
/// <c>WebApplicationFactory</c> test host without touching Program.cs.
/// </summary>
/// <remarks>
/// This is plain path-matching middleware, not a mapped endpoint: the <c>IApplicationBuilder</c>
/// an <see cref="IStartupFilter"/> receives here is a plain <c>ApplicationBuilder</c>, not the
/// <c>WebApplication</c> instance itself, so it never implements <c>IEndpointRouteBuilder</c> and
/// <c>MapGet</c> is not available on it. The middleware is added AFTER <c>next(app)</c> runs
/// Program.cs's own pipeline (UseExceptionHandler, then routing/MapControllers), which is what
/// puts it both downstream of the exception handler (so a thrown exception here is still caught
/// by it) and reachable at all (routing calls onward when no controller endpoint matches these
/// test-only paths).
/// </remarks>
internal sealed class TestEndpointsStartupFilter : IStartupFilter
{
    private const string DomainPath = "/api/test/throw/domain";
    private const string UnexpectedPath = "/api/test/throw/unexpected";
    private const string UnavailablePath = "/api/test/problem/unavailable";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            next(app);

            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path == DomainPath)
                {
                    throw new DomainException("The sku must not be empty.");
                }

                if (context.Request.Path == UnexpectedPath)
                {
                    throw new InvalidOperationException("SECRET-CONNECTION-STRING");
                }

                if (context.Request.Path == UnavailablePath)
                {
                    // The same call site a controller makes - result.Problem(HttpContext) -
                    // executed through the ActionResult machinery it returns to, rather than a
                    // hand-rolled response. That is the point of this path: it proves the 503
                    // and its Retry-After header travel through the exact code a controller
                    // uses, not a reimplementation of it. ActionDescriptor and RouteData carry
                    // no meaning here - ObjectResult's default executor does not consult either
                    // for a body this simple - they exist only because ActionContext requires them.
                    var result = Result.Failure<string>(new Error(
                        ErrorKind.Unavailable,
                        "test.unavailable",
                        "Simulated upstream failure.",
                        RetryAfter: TimeSpan.FromSeconds(5)));

                    var actionContext = new ActionContext(
                        context, context.GetRouteData(), new ActionDescriptor());

                    await result.Problem(context).ExecuteResultAsync(actionContext)
                        .ConfigureAwait(false);
                    return;
                }

                await nextMiddleware(context).ConfigureAwait(false);
            });
        };
    }
}
