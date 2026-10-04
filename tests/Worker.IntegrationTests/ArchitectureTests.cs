using ArchUnitNET.Domain;
using ArchUnitNET.Domain.Dependencies;
using ArchUnitNET.Loader;
using FluentAssertions;

namespace AiFramework.Worker.IntegrationTests;

// No WorkerFactory and no collection: these read the compiled assemblies, never a running host,
// so they need neither Postgres nor RabbitMQ. The Api's twin is
// Api.IntegrationTests/ArchitectureTests.cs; keep the two in step.
public sealed class ArchitectureTests
{
    private const string InfrastructureNamespace = "AiFramework.Infrastructure";

    // The worker's composition code: the only types allowed to name an Infrastructure type ("DI
    // only" in the root CLAUDE.md's dependency table). Jobs and their handlers live in
    // Application and Infrastructure, never here, so the worker should never grow a type that
    // needs this list widened for anything but wiring. Program is listed although ArchUnitNET
    // skips its compiler-generated `<Main>$` - see the Api twin for why.
    private static readonly string[] CompositionTypes =
    [
        "Program",
        "AiFramework.Worker.Observability.WorkerObservability",
    ];

    // Wolverine's handler adapters (`codegen write`) call Infrastructure handlers and middleware
    // directly. They are generated, not written, and the codegen CI job owns them.
    private const string GeneratedNamespace = "Internal.Generated.";

    // Built once: the loader reads every type of both assemblies from IL, and the result is
    // immutable. A type's Dependencies include its method bodies, async and lambda ones too.
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(Program).Assembly, Infrastructure.AssemblyMarker.Assembly)
        .Build();

    // ADR 0016: the worker and the Api are sibling composition roots - a worker that referenced
    // the Api would share its Wolverine generated-code tree. The "AiFramework.*" set is non-empty
    // (Application and Infrastructure), so this assertion is load-bearing.
    [Fact]
    public void Worker_references_no_Api()
    {
        var disallowed = typeof(Program).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null
                && n.StartsWith("AiFramework.", StringComparison.Ordinal)
                && !n.Equals("AiFramework.Domain", StringComparison.Ordinal)
                && !n.Equals("AiFramework.Application", StringComparison.Ordinal)
                && !n.Equals("AiFramework.Infrastructure", StringComparison.Ordinal))
            .ToArray();

        disallowed.Should().BeEmpty(
            "the worker may depend on Domain, Application and Infrastructure, and never on the " +
            "Api - see ADR 0016");
    }

    [Fact]
    public void Only_composition_code_depends_on_Infrastructure()
    {
        var violations = InfrastructureDependencies()
            .Where(d => !IsComposition(d.Origin))
            .Select(d => $"{d.Origin.FullName} -> {d.Target.FullName}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        // Joined rather than asserted as a collection: BeEmpty() on a collection reports only
        // the first item, and a fix wants the whole list.
        string.Join(Environment.NewLine, violations).Should().BeEmpty(
            "only the composition root may name an Infrastructure type; a job belongs in " +
            "Application or Infrastructure, not in the worker host");
    }

    [Fact]
    public void Scan_sees_composition_code_depend_on_Infrastructure()
    {
        var fromObservability = InfrastructureDependencies()
            .Where(d => d.Origin.FullName.Equals(
                "AiFramework.Worker.Observability.WorkerObservability", StringComparison.Ordinal));

        fromObservability.Should().NotBeEmpty(
            "WorkerObservability calls AddInfrastructureTracing(), so an empty result means the " +
            "scan is broken and Only_composition_code_depends_on_Infrastructure would pass for " +
            "the wrong reason");
    }

    // The guard above proves an exempt type is read; this one proves the scan reaches past the
    // composition list. The worker has no other hand-written types, so the generated adapters -
    // which name Infrastructure handlers - are the evidence.
    [Fact]
    public void Scan_sees_generated_adapters_depend_on_Infrastructure()
    {
        var fromAdapters = InfrastructureDependencies()
            .Where(d => d.Origin.Namespace.FullName.StartsWith(
                GeneratedNamespace, StringComparison.Ordinal));

        fromAdapters.Should().NotBeEmpty(
            "the generated adapters call Infrastructure handlers, so an empty result means the " +
            "scan reads only the composition types and the rule has nothing to check");
    }

    private static IEnumerable<ITypeDependency> InfrastructureDependencies() =>
        Architecture.Types
            .Where(t => t.Assembly.FullName.Equals(
                typeof(Program).Assembly.FullName, StringComparison.Ordinal))
            .SelectMany(t => t.Dependencies)
            .Where(d => d.Target.Namespace.FullName.StartsWith(
                InfrastructureNamespace, StringComparison.Ordinal));

    private static bool IsComposition(IType type) =>
        CompositionTypes.Contains(type.FullName, StringComparer.Ordinal)
        || type.Namespace.FullName.StartsWith(GeneratedNamespace, StringComparison.Ordinal);
}
