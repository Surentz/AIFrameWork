namespace AiFramework.Application.Abstractions;

/// <summary>Time, as a dependency. Never call DateTimeOffset.UtcNow directly.</summary>
public interface IClock
{
    public DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Where the current request came from, as the audit needs it. Both properties are null outside a
/// request — the worker has no HTTP context at all, and neither does a background pump.
/// </summary>
/// <remarks>
/// <para>
/// Registered <b>per host</b>, never in <c>AddInfrastructure</c>, exactly as
/// <see cref="ICurrentUser"/> is and for the same reason: the last registration wins, so one made
/// inside a shared registration method silently replaces the host's own.
/// </para>
/// <para>
/// <b>The address is only as trustworthy as the hop in front of it.</b> Behind the ingress that
/// means <c>ForwardedHeaders__Enabled</c> must be on, or every address recorded in the cluster is
/// the ingress pod's rather than the caller's. A single underscore binds nothing and warns
/// nothing. See ADR 0021.
/// </para>
/// </remarks>
public interface IClientContext
{
    public string? IpAddress { get; }

    public string? UserAgent { get; }
}

/// <summary>
/// The signed-in caller, or null when there is no session. Nullable rather than throwing:
/// the outbox pumps resolve scopes with no HTTP context at all, and a handler that returns
/// ErrorKind.Unauthorized produces a 401 where a throw would produce a 500.
/// </summary>
/// <remarks>
/// Once <see cref="Id"/> resolves to a non-null value within a scope, it must keep returning
/// that same value for the rest of the scope — a cacheable query's handler may run later, in
/// that same scope, from a context where re-deriving the caller from scratch is not possible.
/// An implementation that re-reads its source on every access breaks that for any consumer
/// reached through the caching behavior.
/// </remarks>
public interface ICurrentUser
{
    public Guid? Id { get; }
}

/// <summary>
/// Commits the current transaction. COMMAND handlers never call this — the unit-of-work
/// behavior does, exactly once, after a successful command.
/// </summary>
/// <remarks>
/// Domain event handlers are the exception, and it is not a loophole: they run on the outbox
/// pump, which dispatches them directly rather than through the command pipeline, so no behavior
/// is there to commit for them. One that writes anything must therefore save its own work —
/// <c>NotificationFanOut</c> is the example, and <c>OrderAuditWriter</c> avoids the question
/// entirely by issuing an immediate INSERT instead of tracking an entity. A handler that adds to
/// the context and saves nothing writes nothing, while its outbox row is still marked Processed:
/// silent, permanent data loss with no exception anywhere.
/// </remarks>
public interface IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
