using System.Diagnostics;
using AiFramework.Application.Abstractions;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// Counts every HTTP request into the traffic recorder, keyed on its ROUTE TEMPLATE.
/// </summary>
/// <remarks>
/// <para>
/// <b>The template, never the path.</b> <c>/api/orders/{id}</c> is one row; the raw path would be
/// one row per order id, per minute, per pod — cardinality that would make the table useless and
/// large at the same time. A request that matched no endpoint is not recorded at all, for the
/// same reason: a 404 sweep would otherwise write a row per URL somebody guessed.
/// </para>
/// <para>
/// <b>Outcome is read from the status code</b>, which is the only outcome an HTTP request has.
/// 5xx is <see cref="TrafficOutcome.Faulted"/> — the application broke; 4xx is
/// <see cref="TrafficOutcome.Failed"/> — the caller was refused, which is exactly the split
/// <c>Behaviors.LoggedAsync</c> already draws between a thrown exception and a failed
/// <c>Result</c>.
/// </para>
/// <para>
/// <b>Recording happens in a finally.</b> A request that throws still took time and still
/// happened, and it is the one most worth counting.
/// </para>
/// </remarks>
public sealed class TrafficMiddleware(RequestDelegate next, ITrafficRecorder recorder)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            Record(context, (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    private void Record(HttpContext context, long elapsedMs)
    {
        // Resolved AFTER the pipeline has run, because routing is what populates it: read before
        // next(), every request would look unmatched.
        var template = context.GetEndpoint() is RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText
            : null;

        if (string.IsNullOrWhiteSpace(template))
        {
            return;
        }

        var status = context.Response.StatusCode;
        var outcome = status switch
        {
            >= 500 => TrafficOutcome.Faulted,
            >= 400 => TrafficOutcome.Failed,
            _ => TrafficOutcome.Succeeded,
        };

        // The method is part of the key: GET and POST on one template are different work with
        // different latencies, and merging them would average a read into a write.
        recorder.Record(TrafficKind.Http, $"{context.Request.Method} {template}", outcome, elapsedMs);
    }
}
