using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests;

public sealed class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    // Program.cs now throws at startup when ConnectionStrings:Default is null or whitespace,
    // and appsettings.json ships "" for that key. /health never touches the database, so a
    // placeholder value (never a real Testcontainers instance) is enough to satisfy the
    // startup guard without paying for a container this test doesn't need.
    public HealthTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "ConnectionStrings:Default",
                "Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder");

            // A placeholder connection string stopped being enough once the ADR 0005 Wolverine
            // spike was wired in: durable Wolverine migrates its envelope schema during host
            // startup, so the host stopped booting here at all ("Failed to connect to
            // 127.0.0.1:5432") even though /health never touches the database. MediatorOnly
            // turns off envelope storage and with it that startup connection, which keeps this
            // test container-free — the property it was written to have.
            builder.UseSetting("Wolverine:Durable", "false");

            // The same lesson again, one feature later. AdminReconciler dials Postgres at
            // startup to apply the configured administrator list (ADR 0020). It survives an
            // absent database by design - it catches and logs rather than failing the host - but
            // surviving it still means sitting through EnableRetryOnFailure's whole retry budget
            // on every host this fixture builds, for a test whose entire point is that /health
            // needs no database. Off here, exactly as Wolverine's envelope storage is above.
            builder.UseSetting("Admin:ReconcileOnStart", "false");
        });
    }

    [Fact]
    public async Task GetHealth_WhenApplicationIsRunning_Returns200Ok()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
