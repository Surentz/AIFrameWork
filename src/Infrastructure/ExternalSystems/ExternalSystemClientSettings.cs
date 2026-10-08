namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>Code-level settings for one system's typed client, as named options keyed by system name.</summary>
internal sealed class ExternalSystemClientSettings
{
    /// <summary>Non-null: this client never retries, for this reason (ADR 0014's non-idempotent rule).</summary>
    public string? RetryDisabledReason { get; set; }
}
