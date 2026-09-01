using AiFramework.Api.IntegrationTests.Diagnostics;
using AiFramework.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace AiFramework.Api.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // The parameterless PostgreSqlBuilder() + WithImage(...) pairing is obsolete in
    // Testcontainers.PostgreSql 4.14.0 (CS0618); the image now goes to the constructor.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    // The container must start BEFORE anything touches Services: the first access to
    // Services builds the host, which runs ConfigureWebHost, which reads the container's
    // connection string. Reversing these two lines fails with a connection error.
    async Task IAsyncLifetime.InitializeAsync()
    {
        await _container.StartAsync();
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>()
            .Database.MigrateAsync();
    }

    // Explicit interface implementation: WebApplicationFactory already exposes a
    // ValueTask DisposeAsync() from IAsyncDisposable, so declaring xUnit's
    // Task DisposeAsync() implicitly would hide it and leak the host.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await _container.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Program.cs now throws at startup when ConnectionStrings:Default is null or
        // whitespace (see Program.cs). appsettings.json ships "" for that key, so this
        // setting must be supplied before the host is built, not swapped in afterward via
        // ConfigureServices below — otherwise the stricter guard throws first.
        builder.UseSetting("ConnectionStrings:Default", _container.GetConnectionString());

        builder.ConfigureServices(services =>
        {
            var descriptor = services.Single(
                d => d.ServiceType == typeof(DbContextOptions<AiFrameworkDbContext>));
            services.Remove(descriptor);
            services.AddDbContext<AiFrameworkDbContext>(
                options => options.UseNpgsql(_container.GetConnectionString()));

            // Test-only endpoints that throw on demand, exercising GlobalExceptionHandler's
            // two branches over real HTTP. See TestEndpointsStartupFilter for why this is an
            // IStartupFilter rather than a controller.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IStartupFilter, TestEndpointsStartupFilter>());
        });
    }
}
