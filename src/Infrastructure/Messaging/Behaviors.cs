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

        // Grouped by PropertyName, in the same shape ASP.NET Core's ModelState-driven
        // ValidationProblemDetails.Errors uses, so a client can map a message back to a form
        // field instead of only getting one prose blob.
        var details = validation.Errors
            .GroupBy(e => e.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray(), StringComparer.Ordinal);

        return Result.Failure<TResponse>(
            new Error(ErrorKind.Validation, "validation.failed", message, details));
    }

    /// <summary>
    /// Commits exactly once, and only when the command succeeded. Handlers never call
    /// SaveChangesAsync themselves — that is what makes one command one transaction.
    /// Unlike the validator, IUnitOfWork is not absence-tolerant: a missing registration
    /// would otherwise mean the handler reports success while nothing is written, silently.
    /// </summary>
    internal static async Task CommitAsync<TResponse>(
        IServiceProvider sp, Result<TResponse> result, CancellationToken ct)
    {
        if (!result.IsSuccess)
        {
            return;
        }

        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
