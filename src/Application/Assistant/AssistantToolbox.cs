using System.Text.Json;
using System.Text.Json.Serialization;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant.ToolResults;
using AiFramework.Application.Monitoring;

namespace AiFramework.Application.Assistant;

/// <summary>
/// The assistant's tools: each is one monitoring query plus a projection into
/// <see cref="ToolResults"/>. Read-only by construction — no tool dispatches a command. Queries go
/// through <see cref="IQueryDispatcher"/> as the caller, so the assistant sees exactly what that
/// administrator's Monitoring page shows. ADR 0033.
/// </summary>
/// <remarks>
/// Schemas list every property as required, with null allowed where a filter is optional, and set
/// additionalProperties false: that is what strict tool use accepts. Numeric bounds are in the
/// descriptions and enforced here, because strict schemas do not carry them.
/// </remarks>
public sealed class AssistantToolbox(IQueryDispatcher queries)
{
    public const int PageSize = 25;

    public const int MaxWindowHours = 168;

    private const string WindowSchema = """
        {"type":"object","properties":{"window_hours":{"type":"integer","description":"How many hours back to look, 1 to 168."}},"required":["window_hours"],"additionalProperties":false}
        """;

    private const string PageSchema = """
        {"type":"object","properties":{"page":{"type":"integer","description":"Page number, starting at 1. 25 rows per page, newest first."}},"required":["page"],"additionalProperties":false}
        """;

    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<AssistantToolDefinition> Definitions { get; } =
    [
        new("job_health", "Counts of background job runs by outcome (running, succeeded, failed, dead-lettered) over the last window_hours hours.", WindowSchema),
        new("list_job_runs", "One page of background job runs, newest first, optionally filtered by status (Running, Succeeded or Failed) and exact job name. Each row has the job name, attempt, timings, a scrubbed error message and a trace id.", """
            {"type":"object","properties":{"status":{"type":["string","null"],"description":"Running, Succeeded, Failed, or null for all."},"job_name":{"type":["string","null"],"description":"Exact job name, or null for all."},"page":{"type":"integer","description":"Page number, starting at 1. 25 rows per page."}},"required":["status","job_name","page"],"additionalProperties":false}
            """),
        new("get_job_run", "One attempt of one job run, by its envelope id and attempt number.", """
            {"type":"object","properties":{"envelope_id":{"type":"string","description":"The run's envelope id (a GUID)."},"attempt":{"type":"integer","description":"The attempt number, starting at 1."}},"required":["envelope_id","attempt"],"additionalProperties":false}
            """),
        new("list_dead_letters", "One page of messages that exhausted their retries, newest first: message type, exception type, scrubbed exception message, when it was sent, and whether it can be replayed.", PageSchema),
        new("traffic_summary", "Request volume, error rate and latency percentiles over the last window_hours hours, overall and per endpoint or handler.", WindowSchema),
        new("traffic_series", "Per-minute request totals, failures and p95 latency over the last window_hours hours.", WindowSchema),
        new("external_systems", "Every external system the application calls: health state, when it was last checked, certificate expiry, token health and outbound call counts.", """
            {"type":"object","properties":{},"required":[],"additionalProperties":false}
            """),
        new("sign_in_health", "Sign-in counts by outcome over the last window_hours hours, how many users are active now, and how many accounts are locked out. Never names a user.", WindowSchema),
    ];

    public async Task<AssistantToolResult> RunAsync(AssistantToolCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        var outcome = call.Name switch
        {
            "job_health" => await JobHealthAsync(call.Input, cancellationToken).ConfigureAwait(false),
            "list_job_runs" => await JobRunsAsync(call.Input, cancellationToken).ConfigureAwait(false),
            "get_job_run" => await JobRunAsync(call.Input, cancellationToken).ConfigureAwait(false),
            "list_dead_letters" => await DeadLettersAsync(call.Input, cancellationToken).ConfigureAwait(false),
            "traffic_summary" => await TrafficSummaryAsync(call.Input, cancellationToken).ConfigureAwait(false),
            "traffic_series" => await TrafficSeriesAsync(call.Input, cancellationToken).ConfigureAwait(false),
            "external_systems" => await ExternalSystemsAsync(cancellationToken).ConfigureAwait(false),
            "sign_in_health" => await SignInHealthAsync(call.Input, cancellationToken).ConfigureAwait(false),
            _ => Outcome.Fail($"Unknown tool '{call.Name}'."),
        };

        return new AssistantToolResult(call.Id, outcome.Content, outcome.IsError);
    }

    private async Task<Outcome> JobHealthAsync(JsonElement input, CancellationToken ct)
    {
        if (!TryWindow(input, out var window, out var error))
        {
            return error;
        }

        var result = await queries.SendAsync(new GetJobHealth(window), ct).ConfigureAwait(false);
        return Outcome.From(result, static v => new JobHealthResult(v.Running, v.Succeeded, v.Failed, v.DeadLettered, v.Since));
    }

    private async Task<Outcome> JobRunsAsync(JsonElement input, CancellationToken ct)
    {
        if (!TryPage(input, out var page, out var error)
            || !TryOptionalStatus(input, out var status, out error)
            || !TryOptionalString(input, "job_name", out var jobName, out error))
        {
            return error;
        }

        var result = await queries.SendAsync(new GetJobRuns(status, jobName, page, PageSize), ct).ConfigureAwait(false);
        return Outcome.From(result, static v => new JobRunsResult([.. v.Items.Select(ToResult)], v.TotalCount, v.Page));
    }

    private async Task<Outcome> JobRunAsync(JsonElement input, CancellationToken ct)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("envelope_id", out var id)
            || id.ValueKind != JsonValueKind.String
            || !Guid.TryParse(id.GetString(), out var envelopeId))
        {
            return Outcome.Fail("envelope_id must be a GUID.");
        }

        if (!TryPositiveInt(input, "attempt", out var attempt, out var error))
        {
            return error;
        }

        var result = await queries.SendAsync(new GetJobRun(envelopeId, attempt), ct).ConfigureAwait(false);
        return Outcome.From(result, ToResult);
    }

    private async Task<Outcome> DeadLettersAsync(JsonElement input, CancellationToken ct)
    {
        if (!TryPage(input, out var page, out var error))
        {
            return error;
        }

        var result = await queries.SendAsync(new GetDeadLetters(page, PageSize), ct).ConfigureAwait(false);
        return Outcome.From(result, static v => new DeadLettersResult(
            [.. v.Items.Select(static d => new DeadLetterResult(
                d.Id, d.MessageType, d.ExceptionType, OperationalText.Scrub(d.ExceptionMessage), d.SentAt, d.Replayable))],
            v.TotalCount,
            v.Page));
    }

    private async Task<Outcome> TrafficSummaryAsync(JsonElement input, CancellationToken ct)
    {
        if (!TryWindow(input, out var window, out var error))
        {
            return error;
        }

        var result = await queries.SendAsync(new GetTrafficSummary(window), ct).ConfigureAwait(false);
        return Outcome.From(result, static v => new TrafficSummaryResult(
            v.Since, v.RequestsPerMinute, v.ErrorRate, ToResult(v.Overall), [.. v.Rows.Select(ToResult)]));
    }

    private async Task<Outcome> TrafficSeriesAsync(JsonElement input, CancellationToken ct)
    {
        if (!TryWindow(input, out var window, out var error))
        {
            return error;
        }

        var result = await queries.SendAsync(new GetTrafficSeries(window), ct).ConfigureAwait(false);
        return Outcome.From(result, static v => new TrafficSeriesResult(
            v.Since, [.. v.Points.Select(static p => new TrafficPointResult(p.BucketStart, p.Total, p.Failed, p.Faulted, p.P95Ms))]));
    }

    private async Task<Outcome> ExternalSystemsAsync(CancellationToken ct)
    {
        var result = await queries.SendAsync(new GetExternalSystemStatus(), ct).ConfigureAwait(false);
        return Outcome.From(result, static v => new ExternalSystemsResult(
            v.TrafficSince,
            [.. v.Systems.Select(static s => new ExternalSystemResult(
                s.Name, s.State?.ToString(), OperationalText.Scrub(s.Description), s.CheckedAt, s.Stale,
                s.CertificateNotAfter, s.TokenOk, s.Calls, s.Failed, s.Faulted, s.Attempts, s.P95Ms))]));
    }

    private async Task<Outcome> SignInHealthAsync(JsonElement input, CancellationToken ct)
    {
        if (!TryWindow(input, out var window, out var error))
        {
            return error;
        }

        var result = await queries.SendAsync(new GetSignInHealth(window, ActiveWindow), ct).ConfigureAwait(false);
        return Outcome.From(result, static v => new SignInHealthResult(
            v.Succeeded, v.BadCredentials, v.LockedOut, v.UnknownUser, v.Since, v.Active.Count, v.LockedOutUsers.Count));
    }

    private static JobRunResult ToResult(JobRunView v) => new(
        v.EnvelopeId, v.Attempt, v.JobName, v.Lane?.ToString(), v.Status.ToString(), v.StartedAt, v.CompletedAt,
        v.DurationMs, OperationalText.Scrub(v.Error), v.TraceId);

    private static TrafficRowResult ToResult(TrafficRowView v) => new(
        v.Kind.ToString(), v.Name, v.Total, v.Failed, v.Faulted, v.P50Ms, v.P95Ms, v.P99Ms);

    private static bool TryWindow(JsonElement input, out TimeSpan window, out Outcome error)
    {
        window = default;
        if (!TryPositiveInt(input, "window_hours", out var hours, out error))
        {
            error = Outcome.Fail($"window_hours must be a whole number between 1 and {MaxWindowHours}.");
            return false;
        }

        if (hours > MaxWindowHours)
        {
            error = Outcome.Fail($"window_hours must be a whole number between 1 and {MaxWindowHours}.");
            return false;
        }

        window = TimeSpan.FromHours(hours);
        return true;
    }

    private static bool TryPage(JsonElement input, out int page, out Outcome error) =>
        TryPositiveInt(input, "page", out page, out error);

    private static bool TryPositiveInt(JsonElement input, string name, out int value, out Outcome error)
    {
        value = 0;
        error = Outcome.Fail($"{name} must be a whole number of at least 1.");
        return input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value)
            && value >= 1;
    }

    private static bool TryOptionalStatus(JsonElement input, out JobRunStatus? status, out Outcome error)
    {
        status = null;
        error = Outcome.Fail("status must be Running, Succeeded, Failed or null.");
        if (!TryOptionalString(input, "status", out var text, out _))
        {
            return false;
        }

        if (text is null)
        {
            return true;
        }

        // IsDefined rejects numeric strings such as "7", which TryParse would accept.
        if (!Enum.TryParse<JobRunStatus>(text, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return false;
        }

        status = parsed;
        return true;
    }

    private static bool TryOptionalString(JsonElement input, string name, out string? value, out Outcome error)
    {
        value = null;
        error = Outcome.Fail($"{name} must be a string or null.");
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private readonly record struct Outcome(string Content, bool IsError)
    {
        public static Outcome Fail(string message) => new(message, IsError: true);

        public static Outcome From<TView, TResult>(Result<TView> result, Func<TView, TResult> project) =>
            result.IsSuccess
                ? new Outcome(JsonSerializer.Serialize(project(result.Value), Json), IsError: false)
                : Fail(result.Error.Message);
    }
}
