using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using AiFramework.PartnerSimulator;

// The dev PKI for scripts/new-dev-certs.ps1, and the e2e run's (frontend/e2e/setup/prepare-database.ts).
if (args is ["generate-certs", var directory])
{
    using var pki = TestPki.Create("AiFramework Dev");
    pki.WriteDevFiles(directory);
    Console.WriteLine($"Wrote ca.pem, client.pfx/.pass and server.pfx/.pass to {Path.GetFullPath(directory)}");
    return 0;
}

// The dev loop (dev.ps1 -WithPartners) and the e2e run start a real mTLS partner from the files
// `generate-certs` wrote. Health on a plain-HTTP port, so a readiness probe needs no certificate.
if (args is ["serve", var certDirectory, var httpsPort, var healthPort])
{
    var password = await File.ReadAllTextAsync(Path.Combine(certDirectory, "server.pass"));
    using var server = X509CertificateLoader.LoadPkcs12FromFile(
        Path.Combine(certDirectory, "server.pfx"), password.Trim());
    var roots = new X509Certificate2Collection();
    roots.ImportFromPemFile(Path.Combine(certDirectory, "ca.pem"));

    await using var simulator = await PartnerSimulatorApp.StartAsync(
        new PartnerSimulatorOptions
        {
            ServerCertificate = server,
            TrustedClientRoot = roots[0],
            HttpsPort = int.Parse(httpsPort, CultureInfo.InvariantCulture),
            HealthPort = int.Parse(healthPort, CultureInfo.InvariantCulture),
        },
        CancellationToken.None);
    Console.WriteLine($"Partner simulator: {simulator.BaseAddress} (health http://127.0.0.1:{healthPort}/health)");
    await simulator.WaitForShutdownAsync();
    return 0;
}

// The e2e run's stand-in for Anthropic's Messages API (the ops assistant, ADR 0033). Plain HTTP.
if (args is ["messages", var messagesPort])
{
    await using var fake = await FakeMessagesApi.StartAsync(int.Parse(messagesPort, CultureInfo.InvariantCulture));
    fake.DefaultReply = FakeMessagesScript.ToolThenAnswer;
    Console.WriteLine($"Fake Messages API: {fake.BaseAddress} (health {fake.BaseAddress}health)");
    await fake.WaitForShutdownAsync();
    return 0;
}

await Console.Error.WriteLineAsync(
    "Usage: dotnet run --project tests/PartnerSimulator -- generate-certs <directory>" + Environment.NewLine +
    "       dotnet run --project tests/PartnerSimulator -- serve <certDirectory> <httpsPort> <healthPort>" + Environment.NewLine +
    "       dotnet run --project tests/PartnerSimulator -- messages <port>");
return 2;
