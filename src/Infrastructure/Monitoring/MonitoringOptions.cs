namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// How long the monitoring page's own tables keep their rows. Bound from the "Monitoring"
/// configuration section in each host's Program.cs — <c>Monitoring__JobRunRetentionDays</c>,
/// double underscores, like every other key in this application.
/// </summary>
/// <remarks>
/// Bound by the host rather than here, for the reason <c>CacheOptions</c> and <c>JobOptions</c>
/// give: binding in this layer would make the options depend on an IConfiguration that a bare
/// ServiceCollection in a unit test does not have. A host that never binds it gets the default.
/// </remarks>
public sealed class MonitoringOptions
{
    /// <summary>
    /// Thirty days, per ADR 0021. Long enough to answer "what happened last week", short enough
    /// that the table stays a working set rather than an archive.
    /// </summary>
    public int JobRunRetentionDays { get; set; } = 30;

    /// <summary>
    /// Thirty days, per ADR 0021. This table holds an IP address and a user-agent against a
    /// username, so this is not a tuning knob for table size — it is how long this application
    /// keeps personal data, and lengthening it is a decision rather than a default.
    /// </summary>
    public int SignInEventRetentionDays { get; set; } = 30;
}
