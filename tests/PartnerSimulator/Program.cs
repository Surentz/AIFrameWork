using AiFramework.PartnerSimulator;

// Only one command in PR 2: generate the dev PKI for scripts/new-dev-certs.ps1. Serving as a
// standalone container for the dev loop and e2e arrives with PR 3.
if (args is ["generate-certs", var directory])
{
    using var pki = TestPki.Create("AiFramework Dev");
    pki.WriteDevFiles(directory);
    Console.WriteLine($"Wrote ca.pem, client.pfx/.pass and server.pfx/.pass to {Path.GetFullPath(directory)}");
    return 0;
}

await Console.Error.WriteLineAsync("Usage: dotnet run --project tests/PartnerSimulator -- generate-certs <directory>");
return 2;
