using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiFramework.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` at design time, so migrations can be generated without the Api
/// composition root. The connection string is never used to connect — EF needs a provider
/// registered to build the model, not a reachable database.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AiFrameworkDbContext>
{
    public AiFrameworkDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AiFrameworkDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only")
            .Options;

        return new AiFrameworkDbContext(options);
    }
}
