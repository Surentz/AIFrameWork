using System.Reflection;
using System.Reflection.Emit;
using AiFramework.Infrastructure.Observability;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Observability;

/// <summary>
/// What every exported record and span says about which build and which environment produced it.
/// Without these two, a log store holding a week of data cannot tell a regression from the release
/// before it, or a developer's machine from the cluster.
/// </summary>
public sealed class ObservabilityResourceTests
{
    [Fact]
    public void ServiceVersionOf_AnAssemblyWithAnInformationalVersion_IsThatVersionWhole()
    {
        // The "+<sha>" suffix is the point: it is what names the exact commit a pod is running.
        var assembly = AnAssembly(new Version(1, 0, 0, 0), informationalVersion: "1.0.0+abc1234");

        var version = ObservabilityResource.ServiceVersionOf(assembly);

        version.Should().Be("1.0.0+abc1234");
    }

    [Fact]
    public void ServiceVersionOf_AnAssemblyWithoutOne_FallsBackToTheAssemblyVersion()
    {
        var assembly = AnAssembly(new Version(2, 3, 4, 5), informationalVersion: null);

        var version = ObservabilityResource.ServiceVersionOf(assembly);

        version.Should().Be("2.3.4.5");
    }

    [Fact]
    public void DeploymentAttributes_NameTheHostEnvironment()
    {
        var attributes = ObservabilityResource.DeploymentAttributes("Production");

        attributes.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, object>("deployment.environment.name", "Production"));
    }

    private static AssemblyBuilder AnAssembly(Version version, string? informationalVersion)
    {
        var builder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"ServiceVersionProbe{Guid.NewGuid():N}") { Version = version },
            AssemblyBuilderAccess.Run);

        if (informationalVersion is not null)
        {
            var constructor = typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)]);
            // A public attribute constructor that takes exactly one string; it exists on every runtime.
            builder.SetCustomAttribute(new CustomAttributeBuilder(constructor!, [informationalVersion]));
        }

        return builder;
    }
}
