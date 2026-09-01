using AiFramework.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace AiFramework.Api.IntegrationTests.Diagnostics;

/// <summary>
/// Adds two paths that throw on demand, so <c>GlobalExceptionHandler</c>'s two branches
/// (DomainException -> 400, anything else -> 500 with no leaked detail) can be exercised over
/// real HTTP. Registered by <c>ApiFactory</c> as an <see cref="IStartupFilter"/> - the
/// supported way to add test-only middleware to a minimal-hosting-model app (<c>WebApplication</c>)
/// from a <c>WebApplicationFactory</c> test host without touching Program.cs.
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

                await nextMiddleware(context).ConfigureAwait(false);
            });
        };
    }
}
