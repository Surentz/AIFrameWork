using AiFramework.Application.Abstractions;
using Wolverine;

namespace AiFramework.Infrastructure.Jobs;

/// <summary>
/// <see cref="ICurrentUser"/> for a host with no <c>HttpContext</c>. The worker's answer to the
/// question <c>Api/Auth/CurrentUser</c> answers from the cookie's claims.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, and Wolverine creates a scope per message, so one instance serves exactly one job.
/// <see cref="JobUserMiddleware"/> sets it from <see cref="IUserScopedJob.OwnerId"/> before the
/// handler runs.
/// </para>
/// <para>
/// <b>Set-once, like <c>CurrentUser</c>'s memoization, and for a stricter version of the same
/// reason.</b> <see cref="ICurrentUser"/>'s contract requires a stable id for the life of the
/// scope — anything reached through the caching behavior's factory depends on it. A second set
/// within one scope would mean two jobs shared a scope, which is a wiring bug rather than a
/// caller to accommodate, so it throws rather than quietly winning.
/// </para>
/// <para>
/// Left unset, <see cref="Id"/> is null and every ownership-scoped read fails
/// <c>Unauthorized</c> — the correct direction. A job that acts for a user and forgot
/// <see cref="IUserScopedJob"/> does nothing, rather than doing it to everyone.
/// </para>
/// </remarks>
public sealed class JobCurrentUser : ICurrentUser
{
    public Guid? Id { get; private set; }

    public void Set(Guid ownerId)
    {
        if (Id is { } existing)
        {
            throw new InvalidOperationException(
                $"The caller for this job scope is already {existing}; a scope serves one job. " +
                "Two sets means a scope was reused, which is a wiring bug.");
        }

        Id = ownerId;
    }
}

/// <summary>
/// Populates <see cref="JobCurrentUser"/> before a user-scoped job's handler runs.
/// </summary>
/// <remarks>
/// <para>
/// Applied by <c>JobRegistration.IncludeJobHandlers</c> to every chain whose message implements
/// <see cref="IUserScopedJob"/>, so a new user-scoped job cannot forget it. Wolverine discovers
/// <c>Before</c> by convention and generates the call into the handler's adapter — which is why
/// changing this file means re-running the worker's <c>codegen write</c>.
/// </para>
/// <para>
/// <b>Takes the <see cref="Envelope"/>, not an <see cref="IUserScopedJob"/>.</b> JasperFx resolves
/// chain variables by exact type: the chain has the CONCRETE message (<c>BuildOrderExport</c>)
/// and will not upcast it to an interface, so a <c>Before(IUserScopedJob, ...)</c> fails codegen
/// outright with "unable to resolve a variable of type IUserScopedJob". Found by running
/// <c>codegen write</c>, which is the only place it shows up — the code compiles fine.
/// </para>
/// <para>
/// The <c>is</c> check is therefore the cast, and it is also belt-and-braces: the chain predicate
/// already guarantees only user-scoped jobs reach here, so a non-match is not an error to report,
/// just nothing to do.
/// </para>
/// </remarks>
public static class JobUserMiddleware
{
    public static void Before(Envelope envelope, JobCurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(currentUser);

        if (envelope.Message is IUserScopedJob job)
        {
            currentUser.Set(job.OwnerId);
        }
    }
}
