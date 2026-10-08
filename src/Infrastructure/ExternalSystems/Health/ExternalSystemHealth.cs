using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.ExternalSystems.Health;

public static class ExternalSystemHealth
{
    /// <summary>Every external-system check carries this tag.</summary>
    public const string Tag = "external";

    /// <summary>The certificate check's data key for NotAfter — read by the status job (PR 3).</summary>
    public const string CertificateNotAfterKey = "certificateNotAfter";

    /// <summary>
    /// The readiness predicate both hosts pass to /health/ready. A partner outage must never
    /// take a pod out of rotation; without this, MapHealthChecks runs EVERY registered check.
    /// </summary>
    public static bool IsNotExternal(HealthCheckRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return !registration.Tags.Contains(Tag);
    }
}
