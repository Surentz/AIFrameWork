using FluentAssertions;

namespace AiFramework.Infrastructure.Tests;

public sealed class ArchitectureTests
{
    // Infrastructure references both AiFramework.Application (e.g. ICommand<>,
    // AbstractValidator<PlaceOrder>) and AiFramework.Domain (e.g. Order in
    // PlaceOrderHandler), so the "AiFramework.*" set below is genuinely non-empty and this
    // assertion is load-bearing, not vacuous. See Scan_returns_real_assembly_references for
    // the guard against a broken scan.
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
