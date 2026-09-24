namespace AiFramework.Api.IntegrationTests.EventPath;

/// <summary>
/// How long a Wolverine tracking session (<c>host.ExecuteAndWaitAsync</c>) may wait for the
/// messages it set in motion to be fully handled.
/// </summary>
/// <remarks>
/// Wolverine's own default is five seconds, and that was not enough on a loaded CI runner:
/// <c>WolverineOutboxAtomicityTests</c> timed out in <c>backend (Debug)</c> with the message
/// already received at about 230ms, while this assembly ran beside the other test projects and
/// their Postgres containers - then passed on a re-run, and every time locally. Thirty seconds
/// matches <c>Worker.IntegrationTests</c>' tracking sessions. It is a condition wait, not a sleep:
/// it returns the moment the handler finishes, so only a genuinely failing run pays for it.
/// </remarks>
internal static class MessageTracking
{
    public const int TimeoutMs = 30_000;
}
