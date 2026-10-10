namespace AiFramework.Application.Monitoring;

/// <summary>An external system's health as its checks last saw it. The worst of its checks.</summary>
public enum ExternalSystemState
{
    Healthy,
    Degraded,
    Unhealthy,
}

/// <summary>One row of <c>external_system_status</c>: the worker's last look at one system.</summary>
public sealed record ExternalSystemStatusView(
    string Name,
    ExternalSystemState State,
    string? Description,
    DateTimeOffset CheckedAt,
    DateTimeOffset? CertificateNotAfter,
    bool? TokenOk);

/// <summary>Reads what the worker's status publisher wrote. ADR 0031, ADR 0032.</summary>
public interface IExternalSystemStatusReader
{
    public Task<IReadOnlyList<ExternalSystemStatusView>> ListAsync(CancellationToken cancellationToken);
}
