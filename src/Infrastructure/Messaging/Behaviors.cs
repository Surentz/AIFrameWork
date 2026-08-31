using AiFramework.Application.Abstractions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Messaging;

internal static class Behaviors
{
    /// <summary>
    /// Runs the command's validator if one is registered, short-circuiting to a failed Result
    /// before the handler runs. No validator registered means no validation — absence is not
    /// an error, because not every command needs one.
    /// </summary>
    internal static async Task<Result<TResponse>?> ValidateAsync<TCommand, TResponse>(
        IServiceProvider sp, TCommand command, CancellationToken ct)
    {
        var validator = sp.GetService<IValidator<TCommand>>();
        if (validator is null)
        {
            return null;
        }

        var validation = await validator.ValidateAsync(command, ct).ConfigureAwait(false);
        if (validation.IsValid)
        {
            return null;
        }

        var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
        return Result.Failure<TResponse>(
            new Error(ErrorKind.Validation, "validation.failed", message));
    }

    /// <summary>
    /// Commits exactly once, and only when the command succeeded. Handlers never call
    /// SaveChangesAsync themselves — that is what makes one command one transaction.
    /// </summary>
    internal static async Task CommitAsync<TResponse>(
        IServiceProvider sp, Result<TResponse> result, CancellationToken ct)
    {
        if (!result.IsSuccess)
        {
            return;
        }

        var unitOfWork = sp.GetService<IUnitOfWork>();
        if (unitOfWork is not null)
        {
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
