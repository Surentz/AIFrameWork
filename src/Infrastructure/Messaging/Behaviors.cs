using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Caching;
using FluentValidation;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Messaging;

internal static class Behaviors
{
    /// <summary>
    /// Runs the command's validator if one is registered, short-circuiting to a failed Result
    /// before the handler runs. No validator registered means no validation — absence is not
    /// an error, because not every command needs one.
    /// </summary>
    internal static async Task<Result<TResponse>?> ValidateAsync<TCommand, TResponse>(
        IServiceProvider sp, TCommand command, CancellationToken ct)
    {
        var validator = sp.GetService<IValidator<TCommand>>();
        if (validator is null)
        {
            return null;
        }

        var validation = await validator.ValidateAsync(command, ct).ConfigureAwait(false);
        if (validation.IsValid)
        {
            return null;
        }

        var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));

        // Grouped by PropertyName, in the same shape ASP.NET Core's ModelState-driven
        // ValidationProblemDetails.Errors uses, so a client can map a message back to a form
        // field instead of only getting one prose blob.
        var details = validation.Errors
            .GroupBy(e => e.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray(), StringComparer.Ordinal);

        return Result.Failure<TResponse>(
            new Error(ErrorKind.Validation, "validation.failed", message, details));
    }

    /// <summary>
    /// Commits exactly once, and only when the command succeeded. Handlers never call
    /// SaveChangesAsync themselves — that is what makes one command one transaction.
    /// Unlike the validator, IUnitOfWork is not absence-tolerant: a missing registration
    /// would otherwise mean the handler reports success while nothing is written, silently.
    /// </summary>
    internal static async Task CommitAsync<TResponse>(
        IServiceProvider sp, Result<TResponse> result, CancellationToken ct)
    {
        if (!result.IsSuccess)
        {
            return;
        }

        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a query's handler and runs it. Extracted so the cached and uncached paths share
    /// one definition of "run the handler" — AddQuery no longer resolves it directly.
    /// </summary>
    internal static Task<Result<TResponse>> Handle<TQuery, TResponse>(
        IServiceProvider sp, TQuery query, CancellationToken ct)
        where TQuery : IQuery<TResponse>
    {
        var handler = sp.GetRequiredService<IQueryHandler<TQuery, TResponse>>();
        return handler.HandleAsync(query, ct);
    }

    /// <summary>
    /// Serves a query from cache when it opts in with <see cref="ICacheable"/>, and otherwise
    /// runs the handler directly. Caches the success VALUE, not the Result: Result&lt;T&gt;.Error
    /// throws when read on a success, so serializing a successful Result fails, and its internal
    /// constructor makes deserializing one impossible.
    /// </summary>
    internal static async Task<Result<TResponse>> CachedAsync<TQuery, TResponse>(
        IServiceProvider sp, TQuery query, CancellationToken ct)
        where TQuery : IQuery<TResponse>
    {
        // Not absence-tolerant, deliberately, unlike the validator above: an unregistered cache
        // would leave every query silently uncached, the same class of quiet wrongness
        // CommitAsync refuses to allow for a missing IUnitOfWork. AddInfrastructure wires
        // AddCaching in, and CachingRegistrationTests asserts it.
        var options = sp.GetRequiredService<IOptions<CacheOptions>>().Value;

        if (!options.Enabled || query is not ICacheable cacheable)
        {
            return await Handle<TQuery, TResponse>(sp, query, ct).ConfigureAwait(false);
        }

        // A cached query with no caller would share one entry across every user. That is a data
        // leak, so it fails loudly instead: a cacheable query is reachable only from an
        // [Authorize]d endpoint, and its absence means the wiring is wrong.
        var userId = sp.GetRequiredService<ICurrentUser>().Id
            ?? throw new InvalidOperationException(
                $"'{typeof(TQuery).Name}' is ICacheable but there is no current user to scope " +
                "its key to. Cached queries must be reachable only from an authorized endpoint.");

        var cache = sp.GetRequiredService<HybridCache>();
        var name = typeof(TQuery).Name;

        try
        {
            var value = await cache.GetOrCreateAsync(
                CacheScope.Key(name, userId, cacheable.CacheKey),
                (sp, query),
                static async (state, token) =>
                {
                    var result = await Handle<TQuery, TResponse>(state.sp, state.query, token)
                        .ConfigureAwait(false);

                    // The sentinel is what keeps failures out of the cache: HybridCache stores
                    // nothing when its factory throws. The alternative — caching a wrapper and
                    // removing it afterwards — leaves a window in which a concurrent caller
                    // reads the cached failure.
                    return result.IsSuccess
                        ? result.Value
                        : throw new QueryFailedException(result.Error);
                },
                new HybridCacheEntryOptions
                {
                    Expiration = CacheDuration.Clamp(cacheable.Duration, options.MaximumDuration),
                },
                tags: [CacheScope.Tag(name, userId)],
                cancellationToken: ct).ConfigureAwait(false);

            return Result.Success(value);
        }
        catch (QueryFailedException failed)
        {
            return Result.Failure<TResponse>(failed.Error);
        }
    }

    /// <summary>
    /// Removes the caller's cached entries for the query types a command declares, after the
    /// command has committed. Runs in-request, before the response returns, so a client that
    /// refetches immediately after a 201 reads its own write.
    /// </summary>
    /// <remarks>
    /// A failure here is deliberately not caught. With an L1-only HybridCache
    /// RemoveByTagAsync has no realistic failure mode, and catching one would mean
    /// catch (Exception) on a path with no IExceptionHandler parameter, which this repository
    /// bans outside the outbox's exemption. The consequence — a committed write surfacing a 500
    /// — becomes the wrong trade if an L2 tier is ever added. ADR 0009 records that.
    /// <para>
    /// L1-only also means this eviction reaches only the process that handled the write: at more
    /// than one replica, an entry cached by another pod survives it untouched. What keeps the
    /// evicting pod the same pod that serves the caller's next read is the ingress's cookie
    /// session affinity, so removing that annotation silently reintroduces stale reads here.
    /// ADR 0010 records that.
    /// </para>
    /// </remarks>
    internal static async Task EvictAsync<TCommand, TResponse>(
        IServiceProvider sp, TCommand command, Result<TResponse> result, CancellationToken ct)
    {
        if (!result.IsSuccess || command is not IInvalidatesCache invalidates)
        {
            return;
        }

        if (!sp.GetRequiredService<IOptions<CacheOptions>>().Value.Enabled)
        {
            return;
        }

        var userId = sp.GetRequiredService<ICurrentUser>().Id
            ?? throw new InvalidOperationException(
                $"'{typeof(TCommand).Name}' invalidates cache tags but there is no current user " +
                "to scope them to.");

        var cache = sp.GetRequiredService<HybridCache>();

        foreach (var tag in invalidates.Tags)
        {
            await cache.RemoveByTagAsync(CacheScope.Tag(tag, userId), ct).ConfigureAwait(false);
        }
    }

    // CA1032 wants the standard exception constructor set; S3871 wants exception types public.
    // Both are asking to widen a type whose entire purpose is to stay inside one method: it is
    // thrown by the cache factory a dozen lines above and caught by name immediately after,
    // never crosses this class's boundary, and no caller can construct or catch it. Note the
    // catch above names this specific type rather than the base Exception type, so CA1031 is
    // satisfied on its own terms.
#pragma warning disable CA1032, S3871
    private sealed class QueryFailedException(Error error) : Exception
    {
        public Error Error { get; } = error;
    }
#pragma warning restore CA1032, S3871
}
