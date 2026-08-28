using FluentAssertions;
using Xunit;

namespace AiFramework.Domain.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_references_no_other_layer()
    {
        var referenced = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("AiFramework.", StringComparison.Ordinal))
            .ToArray();

        referenced.Should().BeEmpty(
            "Domain is the innermost layer and must reference no other layer");
    }
}
