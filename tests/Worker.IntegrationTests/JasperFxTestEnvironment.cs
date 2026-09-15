using System.Runtime.CompilerServices;
using JasperFx.CommandLine;

namespace AiFramework.Worker.IntegrationTests;

internal static class JasperFxTestEnvironment
{
    /// <summary>
    /// The worker's Program.cs ends in <c>RunJasperFxCommands(args)</c> when args are present, so
    /// that this project's own <c>codegen write</c> is reachable — and WebApplicationFactory
    /// passes args of its own, so every test here takes that branch.
    ///
    /// Without this flag the command runner builds and runs its OWN host rather than handing back
    /// the configured one, and every test fails with "The server has not been started or no web
    /// application was configured." That cost 23 of 24 tests to discover on the Api side; this
    /// file exists so it is not rediscovered here. See tests/Api.IntegrationTests's copy.
    /// </summary>
    // CA2255: the rule exists because a library silently running code at load time surprises its
    // consumers. This is a test project with no consumers, and the alternative — setting the flag
    // in each entry point — is the fragile duplication the rule is meant to avoid.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void AutoStartHostForWebApplicationFactory()
        => JasperFxEnvironment.AutoStartHost = true;
}
