using FluentAssertions;
using Xunit;

namespace AiFramework.Infrastructure.Tests;

public sealed class ArchitectureTests
{
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
}
