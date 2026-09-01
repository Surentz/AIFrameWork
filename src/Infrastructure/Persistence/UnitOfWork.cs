using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.Persistence;

public sealed class UnitOfWork(AiFrameworkDbContext context) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}
