using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiFramework.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` at design time, so migrations can be generated without the Api
/// composition root. `IDesignTimeDbContextFactory` always wins over host-based construction
/// from Api's `Program.cs`, so for most `dotnet ef` verbs (e.g. `migrations add`) the connection
/// string is never used to connect — EF needs a provider registered to build the model, not a
/// reachable database. `database update` is the exception: it must actually connect. The
/// fallback below is a placeholder for the verbs that never dial out; the env var lets e2e
/// global setup (frontend/e2e/global-setup.ts) point a real `database update` at the ephemeral
/// e2e Postgres container without touching appsettings.json.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AiFrameworkDbContext>
{
    public AiFrameworkDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Database=design_time_only";

        var options = new DbContextOptionsBuilder<AiFrameworkDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AiFrameworkDbContext(options);
    }
}
