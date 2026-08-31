using FluentAssertions;

namespace AiFramework.Domain.Tests;

public sealed class ArchitectureTests
{
    // The "AiFramework.*" set below is empty by construction: Domain is the innermost
    // layer, so no other AiFramework assembly can ever appear in its references - this
    // isn't a "not yet" fact that later commits could falsify by adding a type. That
    // emptiness is not itself proof the rule holds - see Scan_returns_real_assembly_references
    // for the guard against a broken scan.
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

    [Fact]
    public void Scan_returns_real_assembly_references()
    {
        var referenced = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referenced.Should().Contain("System.Runtime",
            "an empty or garbage scan would make Domain_references_no_other_layer " +
            "pass for the wrong reason");
    }

    // Banned list mirrors $banned['Domain'] in .claude/hooks/dependency-rule.ps1.
    // That hook only gates .cs files (using directives); this test additionally
    // catches a banned PackageReference/ProjectReference added straight to the
    // .csproj, which the hook cannot see. Keep the two lists in sync by hand.
    [Fact]
    public void Domain_references_no_banned_namespace()
    {
        var bannedPrefixes = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions.DependencyInjection",
            "System.Data",
            "System.ComponentModel.DataAnnotations",
        };

        var violations = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null
                && bannedPrefixes.Any(banned => n.StartsWith(banned, StringComparison.Ordinal)))
            .ToArray();

        violations.Should().BeEmpty(
            "Domain must not reference EF Core, ASP.NET Core, DI, System.Data, " +
            "or DataAnnotations - see the root CLAUDE.md Domain rule");
    }
}
