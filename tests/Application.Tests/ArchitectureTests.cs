using FluentAssertions;

namespace AiFramework.Application.Tests;

public sealed class ArchitectureTests
{
    // Application references AiFramework.Domain (e.g. PlaceOrder uses Domain.Orders.Order),
    // so the "AiFramework.*" set below is genuinely non-empty and this assertion is
    // load-bearing, not vacuous. See tests/CLAUDE.md for why this asserts on the disallowed
    // subset rather than on "contains only Domain" (FluentAssertions 7.2.2's OnlyContain on
    // empty), and Scan_returns_real_assembly_references for the guard against a broken scan.
    [Fact]
    public void Application_references_Domain_only()
    {
        var disallowed = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null
                && n.StartsWith("AiFramework.", StringComparison.Ordinal)
                && !n.Equals("AiFramework.Domain", StringComparison.Ordinal))
            .ToArray();

        disallowed.Should().BeEmpty(
            "Application may depend on Domain and nothing else inward-facing");
    }

    [Fact]
    public void Scan_returns_real_assembly_references()
    {
        var referenced = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referenced.Should().Contain("System.Runtime",
            "an empty or garbage scan would make Application_references_Domain_only " +
            "pass for the wrong reason");
    }
}
