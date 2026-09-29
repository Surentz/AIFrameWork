using System.Reflection;

namespace AiFramework.Infrastructure.Observability;

/// <summary>
/// The OpenTelemetry Resource attributes both hosts stamp beyond <c>service.name</c> and
/// <c>service.instance.id</c>: which build, and which environment. Without them a log store
/// holding a week of data cannot tell a regression from the release before it, or a developer's
/// machine from the cluster.
/// </summary>
/// <remarks>
/// Pure values rather than a <c>ResourceBuilder</c> extension: <c>ResourceBuilder</c> lives in the
/// OpenTelemetry SDK package, and Infrastructure references only <c>OpenTelemetry.Api</c>. Each
/// host passes these to its own <c>ConfigureResource</c>, the same split
/// <see cref="OtlpEndpoint"/> uses.
/// </remarks>
public static class ObservabilityResource
{
    /// <summary>The OpenTelemetry semantic-convention key for the deployment environment.</summary>
    public const string DeploymentEnvironmentName = "deployment.environment.name";

    /// <summary>
    /// The assembly's informational version whole — <c>1.0.0+&lt;commit&gt;</c> when the build
    /// knew its commit (the SDK's SourceLink stamps it from <c>.git</c>, and Dockerfile.api passes
    /// <c>SOURCE_REVISION</c> because the image build has no <c>.git</c>) — else the assembly
    /// version.
    /// </summary>
    public static string ServiceVersionOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? "0.0.0"
            : informational;
    }

    /// <summary>The host environment's name (<c>Development</c>, <c>Production</c>, …).</summary>
    public static IEnumerable<KeyValuePair<string, object>> DeploymentAttributes(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        return [new KeyValuePair<string, object>(DeploymentEnvironmentName, environmentName)];
    }
}
