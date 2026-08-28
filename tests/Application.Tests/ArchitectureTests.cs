using FluentAssertions;
using Xunit;

namespace AiFramework.Application.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Application_references_Domain_only()
    {
        // FluentAssertions 7.2.2's OnlyContain(predicate) throws on an empty collection
        // rather than treating it as vacuously true (verified empirically: Application
        // currently uses no Domain type, so the compiler omits the reference and
        // GetReferencedAssemblies() returns none of "AiFramework.*" here). Asserting on
        // the disallowed subset instead keeps the check correct in both the empty case
        // and the populated case, without relying on that vacuous-truth behaviour.
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
}
