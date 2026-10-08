namespace AiFramework.Application.Abstractions;

/// <summary>What kind of work a traffic measurement counted.</summary>
public enum TrafficKind
{
    /// <summary>An HTTP request, keyed on its ROUTE TEMPLATE rather than its path.</summary>
    Http,

    Command,

    Query,

    /// <summary>
    /// One call to an external system, counted OUTSIDE its retry: its duration includes every
    /// retry, and an open circuit or a timeout is a Faulted call. Name is the system name.
    /// </summary>
    Outbound,

    /// <summary>One physical attempt to an external system, counted INSIDE its retry. More
    /// attempts than calls means the partner is being retried.</summary>
    OutboundAttempt,
}

/// <summary>
/// How one measured unit of work ended. These are exactly <c>Behaviors.LoggedAsync</c>'s own
/// three outcomes, on purpose: the traffic recorder reuses that method's existing timing and
/// classification rather than measuring anything a second time.
/// </summary>
public enum TrafficOutcome
{
    Succeeded,

    /// <summary>Returned a failed <c>Result</c> — the caller was refused, not broken.</summary>
    Failed,

    /// <summary>Threw.</summary>
    Faulted,
}

/// <summary>
/// Counts what the application is doing, for the monitoring page's traffic view. See ADR 0021.
/// </summary>
/// <remarks>
/// <para>
/// <b>In memory, per pod, flushed once a minute.</b> Not a row per request — that was considered
/// and rejected in ADR 0021 on write volume. The cost here is one upsert per (minute, kind, name)
/// per pod, whatever the request rate.
/// </para>
/// <para>
/// <b><see cref="Record"/> must never throw and never block.</b> It runs inline on every request
/// and every dispatch; an observability concern that can fail the work it observes is worse than
/// no observability. Implementations accumulate and return.
/// </para>
/// </remarks>
public interface ITrafficRecorder
{
    public void Record(TrafficKind kind, string name, TrafficOutcome outcome, long elapsedMs);
}
