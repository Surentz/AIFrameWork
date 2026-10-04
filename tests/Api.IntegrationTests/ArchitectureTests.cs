using ArchUnitNET.Domain;
using ArchUnitNET.Domain.Dependencies;
using ArchUnitNET.Loader;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests;

// No ApiFactory and no collection: these read the compiled assemblies, never a running host,
// so they need neither Postgres nor RabbitMQ.
public sealed class ArchitectureTests
{
    private const string InfrastructureNamespace = "AiFramework.Infrastructure";

    // The Api's composition code: the only types allowed to name an Infrastructure type ("DI
    // only" in the root CLAUDE.md's dependency table). Adding to this list widens the rule - a new
    // entry is wiring that calls Add*/Use*, never a controller, hub or service that wants a
    // repository or the DbContext.
    //
    // Program's top-level statements compile into `<Main>$`, which ArchUnitNET skips as
    // compiler-generated, so Program would pass without being listed. It is listed anyway: it is
    // the composition root by definition, and a future loader that does read `<Main>$` must not
    // turn it into a violation.
    private static readonly string[] CompositionTypes =
    [
        "Program",
        "AiFramework.Api.Observability.ObservabilityRegistration",
    ];

    // Wolverine's handler adapters (`codegen write`) call Infrastructure handlers and the
    // DbContext directly. They are generated, not written, and the codegen CI job owns them.
    private const string GeneratedNamespace = "Internal.Generated.";

    // Built once: the loader reads every type of both assemblies from IL, and the result is
    // immutable. A type's Dependencies include its method bodies - async state machines and
    // lambda closures are folded back into the method that declared them - so a DbContext
    // resolved inside an async action is attributed to the controller, not to a hidden type.
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(Program).Assembly, Infrastructure.AssemblyMarker.Assembly)
        .Build();

    // ADR 0016: the Api and the worker are sibling composition roots. The "AiFramework.*" set
    // is non-empty (Application and Infrastructure), so this assertion is load-bearing.
    [Fact]
    public void Api_references_no_Worker()
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
            "the Api may depend on Domain, Application and Infrastructure, and never on the " +
            "worker - see ADR 0016");
    }

    // The .claude/hooks/dependency-rule.ps1 hook cannot tell a controller injecting a repository
    // from a services.AddScoped<>() registration: both are a `using AiFramework.Infrastructure`.
    // This reads every dependency the compiled IL carries - signatures, fields and method
    // bodies, fully-qualified or not - so it catches what the hook cannot.
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
            "only the composition root may name an Infrastructure type; everything else in the " +
            "Api reaches it through an Application port");
    }

    [Fact]
    public void Scan_sees_composition_code_depend_on_Infrastructure()
    {
        var fromRegistration = InfrastructureDependencies()
            .Where(d => d.Origin.FullName.Equals(
                "AiFramework.Api.Observability.ObservabilityRegistration", StringComparison.Ordinal));

        fromRegistration.Should().NotBeEmpty(
            "ObservabilityRegistration calls AddInfrastructureTracing(), so an empty result means " +
            "the scan is broken and Only_composition_code_depends_on_Infrastructure would pass " +
            "for the wrong reason");
    }

    // The guard above proves an exempt type is read; this one proves the types the rule exists
    // for are read too. A loader that dropped them would leave nothing to violate the rule.
    [Fact]
    public void Scan_sees_controllers()
    {
        var controllers = Architecture.Types
            .Where(t => t.FullName.Equals(
                "AiFramework.Api.Orders.OrdersController", StringComparison.Ordinal));

        controllers.Should().NotBeEmpty(
            "an architecture without the Api's controllers would make " +
            "Only_composition_code_depends_on_Infrastructure pass for the wrong reason");
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
