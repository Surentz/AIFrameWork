using System.Net;
using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.ExternalSystems.Http;

/// <summary>
/// Counts calls to one external system. Attached twice per client: as Outbound outside the
/// resilience handler (one per call) and as OutboundAttempt inside it (one per attempt) — the
/// shape egdw_eghealth's OutboundTrafficHandler proved. Records in a finally, so a call that
/// throws is counted; a call the CALLER cancelled is not, because nothing about the partner was
/// learned from it. Never changes what is sent or received, never swallows an exception.
/// </summary>
/// <remarks>
/// Inside the resilience handler the token is Polly's attempt (or total) timeout, linked to the
/// caller's, so "my token is cancelled" cannot tell a timed-out attempt — Faulted, spec §3 — from
/// a caller who gave up. The Outbound instance sits outside it and sees the caller's own token,
/// so it leaves that token on the request for the OutboundAttempt instance to check instead.
/// Without it (a handler used alone), the handler's own token is the caller's.
/// </remarks>
internal sealed class OutboundTrafficHandler(
    ITrafficRecorder recorder, TimeProvider time, string systemName, TrafficKind kind,
    ExternalSystemMetrics? metrics = null) : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<CancellationToken> CallerToken =
        new("AiFramework.ExternalSystems.CallerCancellationToken");

    /// <summary>Outbound or OutboundAttempt: which side of the resilience handler this instance sits on.</summary>
    public TrafficKind Kind => kind;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (kind == TrafficKind.Outbound)
        {
            request.Options.Set(CallerToken, cancellationToken);
        }

        var started = time.GetTimestamp();
        var outcome = TrafficOutcome.Faulted;
        var record = true;
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            outcome = Classify(response.StatusCode);
            return response;
        }
        catch (OperationCanceledException) when (CallerCancelled(request, cancellationToken))
        {
            record = false;
            throw;
        }
        finally
        {
            if (record)
            {
                recorder.Record(kind, systemName, outcome, (long)time.GetElapsedTime(started).TotalMilliseconds);

                // Calls, not attempts: the alert rule wants one count per call, outside retry.
                if (kind == TrafficKind.Outbound)
                {
                    metrics?.RecordCall(systemName, outcome);
                }
            }
        }
    }

    private static bool CallerCancelled(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.Options.TryGetValue(CallerToken, out var caller)
            ? caller.IsCancellationRequested
            : cancellationToken.IsCancellationRequested;

    private static TrafficOutcome Classify(HttpStatusCode status) => (int)status switch
    {
        >= 500 => TrafficOutcome.Faulted,
        >= 400 => TrafficOutcome.Failed,
        _ => TrafficOutcome.Succeeded,
    };
}
