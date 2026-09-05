using System.Runtime.CompilerServices;
using JasperFx.CommandLine;

namespace AiFramework.Api.IntegrationTests;

internal static class JasperFxTestEnvironment
{
    /// <summary>
    /// Program.cs ends in <c>RunJasperFxCommands(args)</c> rather than <c>RunAsync()</c>, so that
    /// `dotnet run --project src/Api -- codegen write` can pre-generate Wolverine's handler
    /// adapters for Release, which ships without the Roslyn compiler (ADR 0005).
    ///
    /// That change breaks WebApplicationFactory on its own: the JasperFx command runner builds and
    /// runs its own host instead of handing back the one the factory configured, and every test
    /// fails with "The server has not been started or no web application was configured."
    /// AutoStartHost is JasperFx's supported switch for exactly this case — its own documentation
    /// calls it "very useful for WebApplicationFactory testing".
    ///
    /// A module initializer rather than a fixture because the flag is static and must be set before
    /// the first host is built, and because HealthTests uses a bare WebApplicationFactory&lt;Program&gt;
    /// rather than ApiFactory — there is no one fixture every test in this assembly shares.
    /// </summary>
    // CA2255: the rule exists because a library silently running code at load time surprises its
    // consumers. This assembly is a test project with no consumers, and the alternative — setting
    // the flag in each entry point — is exactly the fragile duplication the rule is meant to avoid.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void AutoStartHostForWebApplicationFactory()
        => JasperFxEnvironment.AutoStartHost = true;
}
