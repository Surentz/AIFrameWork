using FluentAssertions;

namespace AiFramework.Infrastructure.Tests;

public sealed class ArchitectureTests
{
    // Infrastructure emits exactly one AiFramework reference today: AiFramework.Application
    // (e.g. ICommand<>, PlaceOrder). It emits none to AiFramework.Domain, because no
    // Infrastructure code names a Domain type yet — GetReferencedAssemblies() lists what the
    // compiler actually emitted, not what the project file permits, and Domain is reached
    // transitively through Application. So the scanned set is non-empty and this assertion is
    // load-bearing rather than vacuous, but it is load-bearing on the Application reference
    // alone. Domain stays in the allow-list because Infrastructure MAY depend on it and will
    // once EF maps the Order aggregate. See Scan_returns_real_assembly_references for the
    // guard against a broken scan.
    [Fact]
    public void Infrastructure_references_Application_and_Domain_only()
    {
        var disallowed = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null
                && n.StartsWith("AiFramework.", StringComparison.Ordinal)
                && !n.Equals("AiFramework.Domain", StringComparison.Ordinal)
                && !n.Equals("AiFramework.Application", StringComparison.Ordinal))
            .ToArray();

        disallowed.Should().BeEmpty(
            "Infrastructure may depend on Application and Domain, and nothing else");
    }

    [Fact]
    public void Scan_returns_real_assembly_references()
    {
        var referenced = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referenced.Should().Contain("System.Runtime",
            "an empty or garbage scan would make " +
            "Infrastructure_references_Application_and_Domain_only pass for the wrong reason");
    }
}
