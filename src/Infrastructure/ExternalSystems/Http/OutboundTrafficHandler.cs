using System.Net;
using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.ExternalSystems.Http;

/// <summary>
/// Counts calls to one external system. Attached twice per client: as Outbound outside the
/// resilience handler (one per call) and as OutboundAttempt inside it (one per attempt) — the
/// shape egdw_eghealth's OutboundTrafficHandler proved. Records in a finally, so a call that
/// throws is counted; a call the CALLER cancelled is not, because nothing about the partner was
/// learned from it. Never alters the request or response, never swallows an exception.
/// </summary>
internal sealed class OutboundTrafficHandler(
    ITrafficRecorder recorder, TimeProvider time, string systemName, TrafficKind kind) : DelegatingHandler
{
    /// <summary>Outbound or OutboundAttempt: which side of the resilience handler this instance sits on.</summary>
    public TrafficKind Kind => kind;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var outcome = TrafficOutcome.Faulted;
        var record = true;
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            outcome = Classify(response.StatusCode);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            record = false;
            throw;
        }
        finally
        {
            if (record)
            {
                recorder.Record(kind, systemName, outcome, (long)time.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    private static TrafficOutcome Classify(HttpStatusCode status) => (int)status switch
    {
        >= 500 => TrafficOutcome.Faulted,
        >= 400 => TrafficOutcome.Failed,
        _ => TrafficOutcome.Succeeded,
    };
}
