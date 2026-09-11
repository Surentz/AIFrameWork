using AiFramework.Application.Abstractions;
using AiFramework.Domain.Users;
using FluentValidation;

namespace AiFramework.Application.Users;

public sealed record RegisterUser(string Username, string Password, string DisplayName)
    : ICommand<SessionView>;

public sealed class RegisterUserValidator : AbstractValidator<RegisterUser>
{
    public RegisterUserValidator()
    {
        RuleFor(c => c.Username)
            .NotEmpty()
            .MaximumLength(User.MaxUsernameLength)
            .Must(username => username is not null && Array.TrueForAll(
                username.ToCharArray(),
                c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            .WithMessage(
                "A username can only contain letters, digits, dots, underscores and hyphens.");

        RuleFor(c => c.Password).NotEmpty().MinimumLength(PasswordPolicy.MinimumLength);

        RuleFor(c => c.DisplayName).NotEmpty().MaximumLength(User.MaxDisplayNameLength);
    }
}

public sealed class RegisterUserHandler(IUserRepository users, IPasswordHasher hasher, IClock clock)
    : ICommandHandler<RegisterUser, SessionView>
{
    public async Task<Result<SessionView>> HandleAsync(
        RegisterUser command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Check-then-insert, so a taken name is a friendly 409 rather than a 500. The unique
        // index on UsernameNormalized is still the real guard: two simultaneous registrations of
        // the same name both pass this check, and the second one fails at SaveChanges.
        var taken = await users
            .GetByNormalizedUsernameAsync(User.Normalize(command.Username), cancellationToken)
            .ConfigureAwait(false);

        if (taken is not null)
        {
            return Result.Failure<SessionView>(new Error(
                ErrorKind.Conflict,
                "user.username_taken",
                $"The username '{command.Username}' is already taken."));
        }

        var user = User.Register(
            Guid.NewGuid(),
            command.Username,
            hasher.Hash(command.Password),
            command.DisplayName,
            clock.UtcNow);

        await users.AddAsync(user, cancellationToken).ConfigureAwait(false);

        return Result.Success(
            new SessionView(user.Id, user.Username, user.DisplayName, user.SecurityStamp));
    }
}
