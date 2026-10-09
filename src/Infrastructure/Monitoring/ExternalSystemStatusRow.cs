using AiFramework.Application.Monitoring;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// One external system's last observed health. One row per configured system, upserted by the
/// worker's status publisher every minute (ADR 0032); a system no longer configured is deleted.
/// </summary>
public sealed class ExternalSystemStatusRow
{
    public required string Name { get; init; }

    public required ExternalSystemState State { get; set; }

    public string? Description { get; set; }

    public required DateTimeOffset CheckedAt { get; set; }

    public DateTimeOffset? CertificateNotAfter { get; set; }

    public bool? TokenOk { get; set; }
}
