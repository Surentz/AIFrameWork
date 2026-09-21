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

    /// <summary>
    /// A year — deliberately NOT the thirty days the other audit table keeps, and the difference
    /// is the decision rather than an oversight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>sign_in_events</c> is high-volume operational noise, where a month is generous. A
    /// privilege change is rare, deliberate, and precisely the record somebody wants when
    /// reconstructing how an account came to have access months after the fact — the question
    /// this table exists to answer is one asked late, by definition.
    /// </para>
    /// <para>
    /// The volume argument does not apply either: this table grows with administrative actions,
    /// of which a system has a handful a year, so a longer window costs effectively nothing.
    /// </para>
    /// <para>
    /// It is still personal data and still swept, because "keep it forever" is a different
    /// decision that nobody made. Set <c>Monitoring__AdminActionRetentionDays</c> to 30 to match
    /// the sign-in table if that is preferred — but note the asymmetry before shortening it:
    /// deleted audit rows do not come back, while keeping them is one configuration value away.
    /// </para>
    /// </remarks>
    public int AdminActionRetentionDays { get; set; } = 365;

    /// <summary>
    /// Seven days, per ADR 0021 — shorter than the other two on purpose. This table grows with
    /// request volume rather than with business events, so a week answers "what changed" without
    /// it becoming an archive.
    /// </summary>
    public int TrafficRetentionDays { get; set; } = 7;
}
