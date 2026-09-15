using AiFramework.Application.Abstractions;

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
/// Applied by <c>JobRegistration</c> via
/// <c>opts.Policies.ForMessagesOfType&lt;IUserScopedJob&gt;().AddMiddleware&lt;JobUserMiddleware&gt;()</c>,
/// so it reaches every user-scoped job automatically and a new one cannot forget it. Wolverine
/// discovers <c>Before</c> by convention and generates the call into the handler's adapter —
/// which is why adding or changing this needs <c>codegen write</c> re-run for the worker.
/// </remarks>
public static class JobUserMiddleware
{
    public static void Before(IUserScopedJob job, JobCurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(currentUser);

        currentUser.Set(job.OwnerId);
    }
}
